using MaiaAI.Core.Entities;
using MaiaAI.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace MaiaAI.Infrastructure.DataAccess.Repositories;

public sealed class SqlAuditRepository(IDbContextFactory<AiDbContext> factory) : IAuditRepository
{
    public async Task WriteAsync(AuditLog audit, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.AuditLogs.Add(audit);
        await db.SaveChangesAsync(ct);
    }
}
