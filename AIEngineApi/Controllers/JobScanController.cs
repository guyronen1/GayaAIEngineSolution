using MaiaAI.Core.Entities;
using MaiaAI.Core.Interfaces;
using MaiaAI.Core.Interfaces.UseCases;
using Microsoft.AspNetCore.Mvc;

namespace AIEngineAPI.Controllers;

/// <summary>
/// On-demand scan trigger for MonitoredJobs.
/// Delegates to the IScanStrategy registered for each job's ScanType —
/// no scan-type-specific logic lives here.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class JobScanController(
    IMonitoredJobRepository      jobRepo,
    IEnumerable<IScanStrategy>   strategies,
    IClassifyJobsUseCase         classify,
    IGenerateSuggestionsUseCase  suggest,
    IExecuteFixesUseCase         execute) : ControllerBase
{
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
                var r = await strategy.ScanAsync(job, ct);
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

        var result = await strategy.ScanAsync(job, ct);
        return Ok(result);
    }
}
