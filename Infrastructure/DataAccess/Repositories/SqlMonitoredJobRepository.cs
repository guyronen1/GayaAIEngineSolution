using MaiaAI.Core.Entities;
using MaiaAI.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace MaiaAI.Infrastructure.DataAccess.Repositories;

public sealed class SqlMonitoredJobRepository(IDbContextFactory<AiDbContext> factory)
    : IMonitoredJobRepository
{
    public async Task<List<MonitoredJob>> GetActiveAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.MonitoredJobs
            .Include(m => m.JobType)
            .Include(m => m.ScanTypeDefinition)
            .Include(m => m.ScanCheckRules.Where(r => r.IsActive))
            .Where(m => m.IsActive)
            .ToListAsync(ct);
    }

    public async Task<List<MonitoredJob>> GetActiveWithRulesAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.MonitoredJobs
            .Include(m => m.JobType)
            .Include(m => m.ScanTypeDefinition)
            .Include(m => m.Lease)
            .Include(m => m.ScanCheckRules.Where(r => r.IsActive))
            .Include(m => m.JobRules.Where(jr => jr.IsActive))
                .ThenInclude(jr => jr.Rule)
                    .ThenInclude(r => r!.ErrorType)
            .Where(m => m.IsActive)
            .ToListAsync(ct);
    }

    public async Task<MonitoredJob?> GetByIdAsync(int monitoredJobId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.MonitoredJobs
            .Include(m => m.JobType)
            .Include(m => m.ScanTypeDefinition)
            .Include(m => m.ScanCheckRules.Where(r => r.IsActive))
            .Include(m => m.JobRules).ThenInclude(jr => jr.Rule).ThenInclude(r => r!.ErrorType)
            .FirstOrDefaultAsync(m => m.MonitoredJobId == monitoredJobId, ct);
    }

    public async Task<MonitoredJob?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.MonitoredJobs
            .Include(m => m.JobType)
            .Include(m => m.ScanTypeDefinition)
            .Include(m => m.ScanCheckRules.Where(r => r.IsActive))
            .FirstOrDefaultAsync(m => m.Name == name, ct);
    }

    public async Task<List<ClassificationRule>> GetEffectiveRulesAsync(
        int monitoredJobId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var job = await db.MonitoredJobs
            .Include(m => m.JobRules.Where(jr => jr.IsActive))
                .ThenInclude(jr => jr.Rule)
                    .ThenInclude(r => r!.ErrorType)
            .FirstOrDefaultAsync(m => m.MonitoredJobId == monitoredJobId, ct);

        if (job is null) return [];

        if (job.JobRules.Any())
        {
            return job.JobRules
                .Select(jr => jr.Rule!)
                .Where(r => r.IsActive)
                .OrderBy(r => r.Priority)
                .ToList();
        }

        return await db.ClassificationRules
            .Include(r => r.ErrorType)
            .Where(r => r.JobTypeId == job.JobTypeId && r.IsActive)
            .OrderBy(r => r.Priority)
            .ToListAsync(ct);
    }

    public async Task<List<MonitoredJob>> GetAllWithRulesAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.MonitoredJobs
            .Include(m => m.JobType)
            .Include(m => m.ScanTypeDefinition)
            .Include(m => m.Lease)
            .Include(m => m.ScanCheckRules)
            .Include(m => m.JobRules.Where(jr => jr.IsActive))
                .ThenInclude(jr => jr.Rule).ThenInclude(r => r!.ErrorType)
            .OrderBy(m => m.Name)
            .ToListAsync(ct);
    }

    public async Task<MonitoredJob> SaveAsync(MonitoredJob job, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.MonitoredJobs.Add(job);

        // 1:1 lease row created with the job — immediately eligible (NextEligibleAt = MinValue).
        job.Lease = new MonitoredJobLease { NextEligibleAt = DateTime.MinValue };

        await db.SaveChangesAsync(ct);
        return job;
    }

    public async Task UpdateAsync(MonitoredJob job, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.MonitoredJobs
            .Where(m => m.MonitoredJobId == job.MonitoredJobId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Name,                   job.Name)
                .SetProperty(m => m.DisplayName,            job.DisplayName)
                .SetProperty(m => m.JobTypeId,              job.JobTypeId)
                .SetProperty(m => m.ScanTypeId,             job.ScanTypeId)
                .SetProperty(m => m.LogFolder,              job.LogFolder)
                .SetProperty(m => m.SearchPatterns,         job.SearchPatterns)
                .SetProperty(m => m.ConnectionName,         job.ConnectionName)
                .SetProperty(m => m.LogSourceUrl,           job.LogSourceUrl)
                .SetProperty(m => m.PollingIntervalSeconds, job.PollingIntervalSeconds)
                .SetProperty(m => m.IsActive,               job.IsActive)
                .SetProperty(m => m.Description,            job.Description),
            ct);
    }

    public async Task DeleteAsync(int monitoredJobId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var job = await db.MonitoredJobs.FindAsync([monitoredJobId], ct);
        if (job is null) return;
        job.IsActive = false;
        await db.SaveChangesAsync(ct);
    }
}
