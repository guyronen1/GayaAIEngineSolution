using MaiaAI.Core.Entities;
using MaiaAI.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace MaiaAI.Infrastructure.DataAccess.Repositories;

public sealed class SqlFixLogRepository(IDbContextFactory<AiDbContext> factory) : IFixLogRepository
{
    public async Task SaveAsync(FixExecutionLog log, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.FixExecutionLogs.Add(log);
        await db.SaveChangesAsync(ct);
    }
}
