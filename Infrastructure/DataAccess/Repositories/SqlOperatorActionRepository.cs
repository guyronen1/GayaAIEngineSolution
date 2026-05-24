using MaiaAI.Core.Entities;
using MaiaAI.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace MaiaAI.Infrastructure.DataAccess.Repositories;

public sealed class SqlOperatorActionRepository(IDbContextFactory<AiDbContext> factory)
    : IOperatorActionRepository
{
    public async Task SaveAsync(OperatorAction action, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.OperatorActions.Add(action);
        await db.SaveChangesAsync(ct);
    }
}
