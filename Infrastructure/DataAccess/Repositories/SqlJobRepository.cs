using MaiaAI.Core.Entities;
using MaiaAI.Core.Enums;
using MaiaAI.Core.Interfaces;
using MaiaAI.Core.Results;
using Microsoft.EntityFrameworkCore;

namespace MaiaAI.Infrastructure.DataAccess.Repositories;

public sealed class SqlJobRepository(IDbContextFactory<AiDbContext> factory) : IJobRepository
{
    public async Task<List<JobFailure>> GetByStatusAsync(JobStatus status, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.JobFailures
            .Where(j => j.Status == status)
            .ToListAsync(ct);
    }

    public async Task<JobFailure?> GetByIdAsync(int failureId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.JobFailures
            .Include(j => j.ErrorType)
            .Include(j => j.Recommendations)
            .Include(j => j.MonitoredJob)
            .FirstOrDefaultAsync(j => j.FailureId == failureId, ct);
    }

    public async Task<JobFailure> SaveAsync(JobFailure job, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.JobFailures.Add(job);
        await db.SaveChangesAsync(ct);
        return job;
    }

    public async Task UpdateStatusAsync(int failureId, JobStatus status, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var job = await db.JobFailures.FindAsync([failureId], ct);
        if (job is null) return;
        job.Status = status;
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateClassificationAsync(
        int failureId, ClassificationResult result, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var job = await db.JobFailures.FindAsync([failureId], ct);
        if (job is null) return;
        job.ErrorTypeId = result.ErrorTypeId;
        // Do NOT overwrite job.ErrorMessage — the scan strategy is authoritative for
        // what error was detected (e.g. for FS scans, the specific log line in the new
        // chunk past the watermark). The classifier's RawError already flows to the
        // recommendation's Explanation field via GenerateSuggestionsUseCase.
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<JobFailure>> GetUnclassifiedAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.JobFailures
            .Where(j => j.Status == JobStatus.Failed && j.ErrorTypeId == null)
            .ToListAsync(ct);
    }

    public async Task<bool> HasOpenFailureAsync(
        int monitoredJobId, string sourceTable, string targetField, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.JobFailures.AnyAsync(f =>
            f.MonitoredJobId == monitoredJobId &&
            f.StepName       == sourceTable    &&
            f.Status         != JobStatus.Resolved, ct);
    }

    public async Task<PagedResult<JobFailure>> GetPagedAsync(
        int page, int pageSize, string? view = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        IQueryable<JobFailure> query = db.JobFailures
            .Include(j => j.JobType)
            .Include(j => j.ErrorType)
            .Include(j => j.MonitoredJob);

        // "fix-failed" window is today-midnight, matching the dashboard
        // "Fix Failures Today" KPI it drills into. Captured once so the EF
        // translation doesn't see DateTime.Today inside the Where expression.
        var todayStart = DateTime.Today;

        query = (view ?? string.Empty).ToLowerInvariant() switch
        {
            "active"          => query.Where(j => j.Status == JobStatus.Failed),
            "unclassified"    => query.Where(j => j.Status == JobStatus.Failed && j.ErrorTypeId == null),
            "awaiting-action" => query.Where(j => j.Status == JobStatus.Failed && j.ErrorTypeId != null),
            "resolved"        => query.Where(j => j.Status == JobStatus.Resolved),
            "manual-required" => query.Where(j => j.Status == JobStatus.ManualRequired),
            "auto-fixed"      => query.Where(j => db.FixExecutionLogs.Any(x =>
                                    x.FailureId == j.FailureId && x.Success && x.TriggerType == TriggerType.AutoHeal)),
            "operator-fixed"  => query.Where(j => db.FixExecutionLogs.Any(x =>
                                    x.FailureId == j.FailureId && x.Success && x.TriggerType == TriggerType.OperatorApproved)),
            // Failures the system tried to fix today and failed at — driven
            // by the dashboard's "Fix Failures Today" KPI drill-down. Status
            // is the durable signal (the executor flips JobStatus to
            // ManualRequired on a failed fix); the FixExecutionLog window
            // narrows to "today" so an old failure with a fresh failed log
            // is also surfaced if it ran again today.
            "fix-failed"      => query.Where(j =>
                                    j.Status == JobStatus.ManualRequired
                                 && db.FixExecutionLogs.Any(x =>
                                        x.FailureId == j.FailureId
                                     && !x.Success
                                     && x.ExecutedAt >= todayStart)),
            _ => query, // null / "" / "all" / unknown → no filter
        };

        var ordered = query.OrderByDescending(j => j.DetectedAt);

        var total = await ordered.CountAsync(ct);
        var items = await ordered.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<JobFailure>(items, total, page, pageSize);
    }

    public async Task<HashSet<int>> GetIdsWithRecentFixFailureAsync(
        IReadOnlyCollection<int> failureIds, DateTime since, CancellationToken ct = default)
    {
        if (failureIds.Count == 0) return new HashSet<int>();

        await using var db = await factory.CreateDbContextAsync(ct);
        var hits = await db.FixExecutionLogs
            .Where(x => failureIds.Contains(x.FailureId)
                     && !x.Success
                     && x.ExecutedAt >= since)
            .Select(x => x.FailureId)
            .Distinct()
            .ToListAsync(ct);
        return new HashSet<int>(hits);
    }
}
