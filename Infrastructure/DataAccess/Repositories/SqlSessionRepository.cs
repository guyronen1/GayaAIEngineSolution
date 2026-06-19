using MaiaAI.Core.Entities;
using MaiaAI.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace MaiaAI.Infrastructure.DataAccess.Repositories;

public sealed class SqlSessionRepository(IDbContextFactory<AiDbContext> factory) : ISessionRepository
{
    public async Task<UserSession> CreateAsync(UserSession session, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.UserSessions.Add(session);
        await db.SaveChangesAsync(ct);
        return session;
    }

    public async Task<UserSession?> GetByTokenAsync(string token, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.UserSessions
            .Include(s => s.User)!.ThenInclude(u => u!.Role)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Token == token, ct);
    }

    public async Task UpdateActivityAsync(int sessionId, DateTime lastActivityAt, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.UserSessions
            .Where(s => s.SessionId == sessionId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastActivityAt, lastActivityAt), ct);
    }

    public async Task DeleteByTokenAsync(string token, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.UserSessions
            .Where(s => s.Token == token)
            .ExecuteDeleteAsync(ct);
    }
}
