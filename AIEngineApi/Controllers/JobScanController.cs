using MaiaAI.Core.Entities;
using MaiaAI.Core.Enums;
using MaiaAI.Core.Interfaces;
using MaiaAI.Core.Interfaces.UseCases;
using MaiaAI.Core.Results;
using Microsoft.AspNetCore.Mvc;

namespace AIEngineAPI.Controllers;

/// <summary>
/// On-demand scan trigger for MonitoredJobs.
/// Delegates to the IScanStrategy registered for each job's ScanType —
/// no scan-type-specific logic lives here.
///
/// Manual scans go through <see cref="ExecuteAndRecordAsync"/> which writes a
/// <c>ScanRunHistory</c> row exactly like the background <c>MonitoringWorker</c>
/// does, so the dashboard's recent-activity strip surfaces both paths the same way.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class JobScanController(
    IMonitoredJobRepository      jobRepo,
    IEnumerable<IScanStrategy>   strategies,
    IClassifyJobsUseCase         classify,
    IGenerateSuggestionsUseCase  suggest,
    IExecuteFixesUseCase         execute,
    IScanRunHistoryRepository    historyRepo) : ControllerBase
{
    // Per-process identity for manual triggers — mirrors the worker's LeasedBy format
    // so audit queries can grep "host=...;runId=..." consistently across both paths.
    private static readonly string ManualLeasedByPrefix =
        $"manual;host={Environment.MachineName};pid={Environment.ProcessId}";
    /// <summary>Run the scan pipeline for a MonitoredJob by its ID.</summary>
    [HttpGet("{monitoredJobId:int}")]
    [HttpPost("{monitoredJobId:int}")]
    public async Task<IActionResult> ScanById(int monitoredJobId, CancellationToken ct)
    {
        var job = await jobRepo.GetByIdAsync(monitoredJobId, ct);
        if (job is null)
            return NotFound(new { Message = $"MonitoredJob {monitoredJobId} not found." });

        return await RunScanAsync(job, ct);
    }

    /// <summary>Run the scan pipeline for a MonitoredJob by its Name.</summary>
    [HttpGet("by-name/{name}")]
    [HttpPost("by-name/{name}")]
    public async Task<IActionResult> ScanByName(string name, CancellationToken ct)
    {
        var job = await jobRepo.GetByNameAsync(name, ct);
        if (job is null)
            return NotFound(new { Message = $"MonitoredJob '{name}' not found." });

        return await RunScanAsync(job, ct);
    }

    /// <summary>Run the scan pipeline for ALL active MonitoredJobs immediately.</summary>
    [HttpPost("scan-all")]
    public async Task<IActionResult> ScanAll(CancellationToken ct)
    {
        var jobs    = await jobRepo.GetActiveAsync(ct);
        var results = new List<object>();

        foreach (var job in jobs)
        {
            var strategy = strategies.FirstOrDefault(s => s.ScanType == job.ScanType);
            if (strategy is null)
            {
                results.Add(new { job.MonitoredJobId, job.Name, job.ScanType, Skipped = true,
                                  Reason = $"No strategy registered for ScanType '{job.ScanType}'." });
                continue;
            }

            try
            {
                var r = await ExecuteAndRecordAsync(job, strategy, ct);
                results.Add(new
                {
                    job.MonitoredJobId, job.Name, job.ScanType, Skipped = false,
                    r.FailuresDetected, r.Classifications, r.Recommendations,
                    r.Detail
                });
            }
            catch (Exception ex)
            {
                results.Add(new { job.MonitoredJobId, job.Name, job.ScanType, Skipped = false, Error = ex.Message });
            }
        }

        return Ok(results);
    }

    /// <summary>
    /// Re-classifies all Failed JobFailures that have no ErrorType assigned yet,
    /// then generates suggestions and executes fixes for the newly classified set.
    /// </summary>
    [HttpGet("classify-pending")]
    [HttpPost("classify-pending")]
    public async Task<IActionResult> ClassifyPending(CancellationToken ct)
    {
        var classifications = await classify.ExecuteAsync(ct);
        await suggest.ExecuteAsync(classifications, ct);
        await execute.ExecuteAsync(ct);

        return Ok(new
        {
            Classified   = classifications.Count,
            Suggestions  = classifications.Count,
            FixesQueued  = classifications.Count,
        });
    }

    // ── shared ──────────────────────────────────────────────────────────────

    private async Task<IActionResult> RunScanAsync(MonitoredJob job, CancellationToken ct)
    {
        var strategy = strategies.FirstOrDefault(s => s.ScanType == job.ScanType);
        if (strategy is null)
            return BadRequest(new { Message = $"No scan strategy registered for ScanType '{job.ScanType}'." });

        var result = await ExecuteAndRecordAsync(job, strategy, ct);
        return Ok(result);
    }

    /// <summary>
    /// Runs the scan and appends a ScanRunHistory row regardless of outcome. Mirrors
    /// the finally-block in <c>MonitoringWorker.RunOneJobAsync</c> so manual triggers
    /// surface in the dashboard's recent-activity feed the same way scheduled scans do.
    /// History-write failures are swallowed — they must not poison the operator's
    /// scan response.
    /// </summary>
    private async Task<ScanResult> ExecuteAndRecordAsync(
        MonitoredJob job, IScanStrategy strategy, CancellationToken ct)
    {
        var leasedBy  = $"{ManualLeasedByPrefix};runId={Guid.NewGuid():N}";
        var startedAt = DateTime.Now;
        var outcome   = JobRunOutcome.Success;
        string? error = null;

        ScanResult? result = null;
        try
        {
            result = await strategy.ScanAsync(job, ct);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            outcome = JobRunOutcome.Timeout;
            error   = "Scan cancelled by client";
            throw;
        }
        catch (Exception ex)
        {
            outcome = JobRunOutcome.Failed;
            error   = ex.Message;
            throw;
        }
        finally
        {
            var completedAt = DateTime.Now;
            try
            {
                var durationMs = (int)Math.Clamp((completedAt - startedAt).TotalMilliseconds, 0, int.MaxValue);
                await historyRepo.SaveAsync(new ScanRunHistory
                {
                    MonitoredJobId   = job.MonitoredJobId,
                    LeasedBy         = leasedBy,
                    StartedAt        = startedAt,
                    CompletedAt      = completedAt,
                    DurationMs       = durationMs,
                    Outcome          = outcome,
                    Error            = error is null ? null : (error.Length > 2000 ? error[..2000] : error),
                    FailuresDetected = result?.FailuresDetected ?? 0,
                    Classifications  = result?.Classifications  ?? 0,
                    Recommendations  = result?.Recommendations  ?? 0,
                }, ct);
            }
            catch { /* don't let history-write failures affect the scan response */ }
        }
    }
}
