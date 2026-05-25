using MaiaAI.Core.Enums;
using MaiaAI.Core.Interfaces;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace MaiaAI.Infrastructure.DataAccess.Repositories;

public sealed class SqlMonitoredJobLeaseRepository(IDbContextFactory<AiDbContext> factory)
    : IMonitoredJobLeaseRepository
{
    // Single atomic UPDATE TOP (N) ... OUTPUT inserted.* with READPAST so concurrent
    // workers walk past each other's locked rows rather than blocking. UPDLOCK + ROWLOCK
    // hold the row until commit so post-update state is visible to the other tx (or skipped).
    private const string ClaimSql = """
        DECLARE @now datetime2(3) = SYSDATETIME();

        UPDATE TOP (@batchSize) L
        SET
            LeasedBy         = @leasedBy,
            LeasedAt         = @now,
            LeasedUntil      = DATEADD(SECOND, S.LeaseDurationSeconds, @now),
            LastRunStartedAt = @now,
            LastRunOutcome   = NULL,
            LastRunError     = NULL
        OUTPUT
            inserted.MonitoredJobId  AS MonitoredJobId,
            S.LeaseDurationSeconds   AS LeaseDurationSeconds,
            inserted.LeasedUntil     AS LeasedUntil
        FROM dbo.MonitoredJobLeases L WITH (READPAST, UPDLOCK, ROWLOCK)
        JOIN dbo.MonitoredJobs      J ON J.MonitoredJobId = L.MonitoredJobId
        JOIN dbo.ScanTypes          S ON S.ScanTypeId     = J.ScanTypeId
        WHERE J.IsActive = 1
          AND L.NextEligibleAt <= @now
          AND (L.LeasedUntil IS NULL OR L.LeasedUntil < @now);
        """;

    public async Task<IReadOnlyList<ClaimedJobLease>> ClaimAsync(
        string leasedBy, int batchSize, CancellationToken ct)
    {
        if (batchSize <= 0) return Array.Empty<ClaimedJobLease>();

        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = db.Database.GetDbConnection();  // EF-owned: do NOT dispose
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = ClaimSql;
        cmd.Parameters.Add(new SqlParameter("@leasedBy",  leasedBy));
        cmd.Parameters.Add(new SqlParameter("@batchSize", batchSize));

        var claimed = new List<ClaimedJobLease>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            claimed.Add(new ClaimedJobLease(
                MonitoredJobId:       reader.GetInt32(0),
                LeaseDurationSeconds: reader.GetInt32(1),
                LeasedUntil:          reader.GetDateTime(2)));
        }
        return claimed;
    }

    public async Task<bool> ReleaseAsync(
        int monitoredJobId, string leasedBy, JobRunOutcome outcome,
        int nextPollingIntervalSeconds, string? error, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        // ExecuteUpdate with the LeasedBy guard — if another worker stole the lease,
        // RowsAffected will be 0 and we leave their state alone.
        var truncatedError = error is null
            ? null
            : error.Length > 2000 ? error[..2000] : error;

        var rows = await db.MonitoredJobLeases
            .Where(l => l.MonitoredJobId == monitoredJobId && l.LeasedBy == leasedBy)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.LeasedBy,           (string?)null)
                .SetProperty(l => l.LeasedUntil,        (DateTime?)null)
                .SetProperty(l => l.LastRunCompletedAt, DateTime.Now)
                .SetProperty(l => l.LastRunOutcome,     (JobRunOutcome?)outcome)
                .SetProperty(l => l.LastRunError,       truncatedError)
                .SetProperty(l => l.NextEligibleAt,     DateTime.Now.AddSeconds(nextPollingIntervalSeconds)),
            ct);

        return rows > 0;
    }

    public async Task<bool> HeartbeatAsync(
        int monitoredJobId, string leasedBy, int extendSeconds, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var rows = await db.MonitoredJobLeases
            .Where(l => l.MonitoredJobId == monitoredJobId
                     && l.LeasedBy == leasedBy
                     && l.LeasedUntil != null
                     && l.LeasedUntil > DateTime.Now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.LeasedUntil, DateTime.Now.AddSeconds(extendSeconds)),
            ct);

        return rows > 0;
    }
}
