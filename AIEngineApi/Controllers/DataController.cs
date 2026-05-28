using AIEngineAPI.Contracts;
using MaiaAI.Core.Enums;
using MaiaAI.Core.Interfaces;
using MaiaAI.Infrastructure.DataAccess;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AIEngineAPI.Controllers;

/// <summary>
/// Read-only query endpoints for the monitoring dashboard.
/// Uses Core repository interfaces — no EF or Infrastructure types except a
/// read-only <c>IDbContextFactory</c> for aggregate dashboard stats.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class DataController(
    IJobRepository                 jobs,
    IRecommendationRepository      recommendations,
    IMonitoredJobRepository        monitoredJobs,
    IScanRunHistoryRepository      scanRuns,
    IDbContextFactory<AiDbContext> dbFactory) : ControllerBase
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
        [FromQuery] int     page     = 1,
        [FromQuery] int     pageSize = 50,
        [FromQuery] string? view     = null,
        CancellationToken   ct       = default)
    {
        var paged = await jobs.GetPagedAsync(page, pageSize, view, ct);
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
    /// Liveness + active-scan snapshot for the dashboard. Polled every few seconds, so
    /// kept to a single SELECT that pulls the lease state for every active job, joined
    /// with its MonitoredJob + ScanType. Computes <c>aliveWindowSeconds</c> from the
    /// active-jobs' polling intervals so the client doesn't need its own threshold config.
    /// </summary>
    [HttpGet("worker-status")]
    public async Task<IActionResult> GetWorkerStatus(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var nowLocal = DateTime.Now;
        var recentCutoff = nowLocal.AddSeconds(-30);

        // Single round-trip: project just the fields we need (no Includes), shapes flat.
        var rows = await db.MonitoredJobLeases
            .Where(l => l.MonitoredJob != null && l.MonitoredJob.IsActive)
            .Select(l => new
            {
                l.MonitoredJobId,
                JobName                = l.MonitoredJob!.Name,
                ScanTypeName           = l.MonitoredJob.ScanTypeDefinition != null
                                            ? l.MonitoredJob.ScanTypeDefinition.Name
                                            : "Unknown",
                PollingIntervalSeconds = l.MonitoredJob.PollingIntervalSeconds,
                l.LeasedBy,
                l.LeasedAt,
                l.LeasedUntil,
                l.LastRunCompletedAt,
                l.LastRunOutcome,
            })
            .ToListAsync(ct);

        // Recent-completion window (last 30s) — what filled in between polls.
        // Joined to MonitoredJobs so the client can render job name without another lookup.
        var recentScans = await db.ScanRunHistory
            .Where(h => h.CompletedAt >= recentCutoff && h.MonitoredJob != null)
            .OrderByDescending(h => h.CompletedAt)
            .Take(20)
            .Select(h => new
            {
                scanRunId        = h.ScanRunId,
                monitoredJobId   = h.MonitoredJobId,
                jobName          = h.MonitoredJob!.Name,
                completedAt      = h.CompletedAt,
                durationMs       = h.DurationMs,
                outcome          = h.Outcome.ToString(),
                failuresDetected = h.FailuresDetected,
                classifications  = h.Classifications,
                recommendations  = h.Recommendations,
            })
            .ToListAsync(ct);

        // Per-job latest-scan summary for the dashboard's Monitored Jobs panel.
        // Correlated subquery uses IX_ScanRunHistory_Job_StartedAt for a seek + top-1.
        // Thin payload: only id + lastScan; name/scanType already live in the static
        // MonitoredJobDto the panel uses on initial load.
        var jobs = await db.MonitoredJobs
            .Where(m => m.IsActive)
            .Select(m => new
            {
                monitoredJobId = m.MonitoredJobId,
                lastScan = db.ScanRunHistory
                    .Where(h => h.MonitoredJobId == m.MonitoredJobId)
                    .OrderByDescending(h => h.StartedAt)
                    .Select(h => new
                    {
                        completedAt      = h.CompletedAt,
                        durationMs       = h.DurationMs,
                        outcome          = h.Outcome.ToString(),
                        failuresDetected = h.FailuresDetected,
                        classifications  = h.Classifications,
                        recommendations  = h.Recommendations,
                    })
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        var activeScans = rows
            .Where(r => r.LeasedBy != null && r.LeasedUntil.HasValue && r.LeasedUntil > nowLocal)
            .Select(r => new
            {
                monitoredJobId = r.MonitoredJobId,
                jobName        = r.JobName,
                scanType       = r.ScanTypeName,
                startedAt      = r.LeasedAt,
                leasedUntil    = r.LeasedUntil,
            })
            .ToList();

        // Alive-window threshold: 2 × max(PollingIntervalSeconds) across active jobs.
        // Fallback to 300s when there are no active jobs at all (defensive — keeps the
        // window finite for the workerAlive calculation below).
        var maxPolling = rows.Count > 0 ? rows.Max(r => r.PollingIntervalSeconds) : 300;
        var aliveWindowSeconds = maxPolling * 2;

        var lastCompletion = rows
            .Where(r => r.LastRunCompletedAt.HasValue)
            .Select(r => r.LastRunCompletedAt!.Value)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();
        var lastActivityAt = activeScans.Count > 0
            ? nowLocal
            : lastCompletion == DateTime.MinValue ? (DateTime?)null : lastCompletion;

        var workerAlive = activeScans.Count > 0
            || (lastActivityAt.HasValue
                && (nowLocal - lastActivityAt.Value).TotalSeconds < aliveWindowSeconds);

        var jobSummary = new
        {
            total   = rows.Count,
            active  = activeScans.Count,
            healthy = rows.Count(r => r.LastRunOutcome == JobRunOutcome.Success),
            failing = rows.Count(r => r.LastRunOutcome.HasValue
                                   && r.LastRunOutcome.Value != JobRunOutcome.Success),
        };

        return Ok(new
        {
            workerAlive,
            lastActivityAt,
            aliveWindowSeconds,
            activeScans,
            recentScansLast30s = recentScans,
            jobSummary,
            jobs,
        });
    }

    /// <summary>
    /// Time-bucketed failure counts broken out by ErrorType, for the dashboard's
    /// Errors Over Time chart. <paramref name="range"/> selects the look-back window;
    /// <paramref name="bucketSize"/> defaults to hour for 24h and day for 7d/30d.
    /// Unclassified failures (ErrorTypeId IS NULL) collapse into a single
    /// "(unclassified)" series with errorTypeId=0 so the frontend can render them
    /// alongside the named series.
    /// </summary>
    [HttpGet("analytics/failures-over-time")]
    public async Task<IActionResult> GetFailuresOverTime(
        [FromQuery] string  range      = "24h",
        [FromQuery] string? bucketSize = null,
        CancellationToken   ct         = default)
    {
        // Range → look-back start (server-local). Use DateTime.Now (not UTC) to stay
        // consistent with the rest of the codebase per CLAUDE.md.
        var nowLocal = DateTime.Now;
        DateTime start;
        string effectiveBucket;
        switch ((range ?? "24h").ToLowerInvariant())
        {
            case "7d":  start = nowLocal.AddDays(-7);  effectiveBucket = bucketSize ?? "day";  break;
            case "30d": start = nowLocal.AddDays(-30); effectiveBucket = bucketSize ?? "day";  break;
            case "24h":
            default:    start = nowLocal.AddHours(-24); effectiveBucket = bucketSize ?? "hour"; break;
        }
        if (effectiveBucket != "hour" && effectiveBucket != "day")
            return BadRequest(new { Message = "bucketSize must be 'hour' or 'day'." });

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Server-side group by truncated DetectedAt + ErrorTypeId. EF translates
        // DateTime constructors to SQL date-part expressions in modern providers.
        // For SQL Server we lean on DATEPART/DATEADD via a switch on bucket size.
        var rows = effectiveBucket == "hour"
            ? await db.JobFailures
                .Where(f => f.DetectedAt >= start)
                .GroupBy(f => new
                {
                    BucketStart = new DateTime(f.DetectedAt.Year, f.DetectedAt.Month, f.DetectedAt.Day, f.DetectedAt.Hour, 0, 0),
                    f.ErrorTypeId,
                })
                .Select(g => new
                {
                    bucketStart = g.Key.BucketStart,
                    errorTypeId = g.Key.ErrorTypeId,
                    count       = g.Count(),
                })
                .ToListAsync(ct)
            : await db.JobFailures
                .Where(f => f.DetectedAt >= start)
                .GroupBy(f => new
                {
                    BucketStart = new DateTime(f.DetectedAt.Year, f.DetectedAt.Month, f.DetectedAt.Day),
                    f.ErrorTypeId,
                })
                .Select(g => new
                {
                    bucketStart = g.Key.BucketStart,
                    errorTypeId = g.Key.ErrorTypeId,
                    count       = g.Count(),
                })
                .ToListAsync(ct);

        // One round-trip for ErrorType metadata (used to display human names client-side).
        var errorTypeIds = rows.Where(r => r.errorTypeId.HasValue).Select(r => r.errorTypeId!.Value).Distinct().ToList();
        var errorTypes = await db.ErrorTypes
            .Where(et => errorTypeIds.Contains(et.ErrorTypeId))
            .ToDictionaryAsync(et => et.ErrorTypeId, et => new { et.Code, et.DisplayName }, ct);

        var result = rows
            .OrderBy(r => r.bucketStart)
            .ThenBy(r => r.errorTypeId)
            .Select(r =>
            {
                if (r.errorTypeId is int id && errorTypes.TryGetValue(id, out var et))
                {
                    return new
                    {
                        bucketStart      = r.bucketStart,
                        errorTypeId      = id,
                        errorTypeCode    = et.Code,
                        errorTypeDisplay = et.DisplayName,
                        count            = r.count,
                    };
                }
                return new
                {
                    bucketStart      = r.bucketStart,
                    errorTypeId      = 0,
                    errorTypeCode    = "(unclassified)",
                    errorTypeDisplay = "Unclassified",
                    count            = r.count,
                };
            })
            .ToList();

        return Ok(new
        {
            range          = range,
            bucketSize     = effectiveBucket,
            rangeStart     = start,
            rangeEnd       = nowLocal,
            buckets        = result,
        });
    }

    /// <summary>
    /// Aggregate counts for the dashboard. DB-level — does not depend on paging.
    /// <c>autoFixed</c> counts distinct failures with a successful auto-heal execution log;
    /// <c>manuallyFixed</c> counts distinct failures resolved via operator approval.
    /// </summary>
    [HttpGet("dashboard-stats")]
    public async Task<IActionResult> GetDashboardStats(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var totalFailures   = await db.JobFailures.CountAsync(ct);
        var active          = await db.JobFailures.CountAsync(f => f.Status == JobStatus.Failed, ct);
        var resolved        = await db.JobFailures.CountAsync(f => f.Status == JobStatus.Resolved, ct);
        var manualRequired  = await db.JobFailures.CountAsync(f => f.Status == JobStatus.ManualRequired, ct);
        var unclassified    = await db.JobFailures.CountAsync(f => f.Status == JobStatus.Failed && f.ErrorTypeId == null, ct);
        var awaitingAction  = await db.JobFailures.CountAsync(f => f.Status == JobStatus.Failed && f.ErrorTypeId != null, ct);

        var autoFixed       = await db.FixExecutionLogs
            .Where(x => x.Success && x.TriggerType == TriggerType.AutoHeal)
            .Select(x => x.FailureId).Distinct().CountAsync(ct);

        var manuallyFixed   = await db.FixExecutionLogs
            .Where(x => x.Success && x.TriggerType == TriggerType.OperatorApproved)
            .Select(x => x.FailureId).Distinct().CountAsync(ct);

        // Today-scoped fields for the "Resolved Today" KPI tile + its breakdown line.
        // "Today" = server-local midnight to now (DateTime.Today, not UTC) — matches
        // the local-time convention documented in CLAUDE.md.
        var todayStart      = DateTime.Today;
        var resolvedToday   = await db.JobFailures.CountAsync(
            f => f.Status == JobStatus.Resolved && f.DetectedAt >= todayStart, ct);
        var autoFixedToday  = await db.JobFailures
            .Where(f => f.DetectedAt >= todayStart)
            .CountAsync(f => db.FixExecutionLogs.Any(x =>
                x.FailureId == f.FailureId && x.Success && x.TriggerType == TriggerType.AutoHeal), ct);
        var manuallyFixedToday = await db.JobFailures
            .Where(f => f.DetectedAt >= todayStart)
            .CountAsync(f => db.FixExecutionLogs.Any(x =>
                x.FailureId == f.FailureId && x.Success && x.TriggerType == TriggerType.OperatorApproved), ct);

        return Ok(new
        {
            totalFailures,
            active,
            resolved,
            manualRequired,
            unclassified,
            awaitingAction,
            autoFixed,
            manuallyFixed,
            resolvedToday,
            autoFixedToday,
            manuallyFixedToday,
        });
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
