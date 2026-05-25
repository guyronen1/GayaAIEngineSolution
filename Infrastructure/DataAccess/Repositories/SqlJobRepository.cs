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
        if (!string.IsNullOrWhiteSpace(result.RawError))
            job.ErrorMessage = result.RawError;
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
        int page, int pageSize, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var query = db.JobFailures
            .Include(j => j.JobType)
            .Include(j => j.ErrorType)
            .Include(j => j.MonitoredJob)
            .OrderByDescending(j => j.DetectedAt);

        var total = await query.CountAsync(ct);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<JobFailure>(items, total, page, pageSize);
    }
}
