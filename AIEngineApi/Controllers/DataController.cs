using AIEngineAPI.Contracts;
using MaiaAI.Core.Enums;
using MaiaAI.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace AIEngineAPI.Controllers;

/// <summary>
/// Read-only query endpoints for the monitoring dashboard.
/// Uses Core repository interfaces — no EF or Infrastructure types in this layer.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class DataController(
    IJobRepository             jobs,
    IRecommendationRepository  recommendations,
    IMonitoredJobRepository    monitoredJobs,
    IScanRunHistoryRepository  scanRuns) : ControllerBase
{
    private const int MaxPageSize = 200;
    [HttpGet("recommendations")]
    public async Task<IActionResult> GetRecommendations(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var paged = await recommendations.GetPagedAsync(page, pageSize, ct);
        var dtos  = paged.Items.Select(RecommendationDto.From).ToList();
        return Ok(new { paged.TotalCount, paged.TotalPages, paged.Page, paged.PageSize, Items = dtos });
    }

    [HttpGet("failures/{failureId:int}/status")]
    public async Task<IActionResult> GetFailureStatus(int failureId, CancellationToken ct)
    {
        var f = await jobs.GetByIdAsync(failureId, ct);
        if (f is null)
            return NotFound(new { Message = $"JobFailure {failureId} not found." });

        var hasRecommendation = f.Recommendations.Any();
        var isExecuted        = f.Recommendations.Any(r => r.IsExecuted);

        var stage = f.Status == MaiaAI.Core.Enums.JobStatus.Resolved || isExecuted ? "Fixed"
                  : hasRecommendation                                               ? "Recommended"
                  : f.ErrorTypeId.HasValue                                          ? "Classified"
                  :                                                                   "Failed";

        return Ok(new
        {
            f.FailureId,
            f.SourceId,
            f.StepName,
            f.ErrorMessage,
            f.DetectedAt,
            Status           = f.Status.ToString(),
            Stage            = stage,
            ErrorTypeCode    = f.ErrorType?.Code,
            MonitoredJobName = f.MonitoredJob?.Name,
            Recommendations  = f.Recommendations.Select(r => new
            {
                r.RecommendationId,
                r.SuggestedAction,
                FixCategory    = r.FixCategory.ToString(),
                r.ConfidenceScore,
                r.AutoFixAvailable,
                r.OperatorApproved,
                r.IsExecuted,
                r.RecommendedAt,
            }).ToList(),
        });
    }

    [HttpGet("failures")]
    public async Task<IActionResult> GetFailures(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var paged = await jobs.GetPagedAsync(page, pageSize, ct);
        var dtos  = paged.Items.Select(JobFailureDto.From).ToList();
        return Ok(new { paged.TotalCount, paged.TotalPages, paged.Page, paged.PageSize, Items = dtos });
    }

    [HttpGet("monitored-jobs")]
    public async Task<IActionResult> GetMonitoredJobs(CancellationToken ct)
    {
        var all  = await monitoredJobs.GetActiveWithRulesAsync(ct);
        var dtos = all.Select(MonitoredJobDto.From).ToList();
        return Ok(dtos);
    }

    /// <summary>
    /// One row per completed worker-tick scan. Filterable by job, outcome, and date range.
    /// Default sort: StartedAt DESC. Default page size 50, max 200.
    /// </summary>
    [HttpGet("scan-runs")]
    public async Task<IActionResult> GetScanRuns(
        [FromQuery] int?      monitoredJobId,
        [FromQuery] string?   outcome,
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        [FromQuery] int       page     = 1,
        [FromQuery] int       pageSize = 50,
        CancellationToken     ct       = default)
    {
        if (page < 1)     page     = 1;
        if (pageSize < 1) pageSize = 1;
        if (pageSize > MaxPageSize) pageSize = MaxPageSize;

        JobRunOutcome? outcomeFilter = null;
        if (!string.IsNullOrWhiteSpace(outcome))
        {
            if (!Enum.TryParse<JobRunOutcome>(outcome, ignoreCase: true, out var parsed))
                return BadRequest(new { Message = $"Unknown outcome '{outcome}'. Expected one of: Success, Failed, Timeout, Stolen." });
            outcomeFilter = parsed;
        }

        var paged = await scanRuns.GetPagedAsync(
            monitoredJobId, outcomeFilter, fromDate, toDate, page, pageSize, ct);
        var dtos  = paged.Items.Select(ScanRunDto.From).ToList();
        return Ok(new { paged.TotalCount, paged.TotalPages, paged.Page, paged.PageSize, Items = dtos });
    }
}
