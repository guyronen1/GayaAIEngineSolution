using MaiaAI.Core.Entities;
using MaiaAI.Core.Interfaces;
using MaiaAI.Core.Results;
using Microsoft.EntityFrameworkCore;

namespace MaiaAI.Infrastructure.DataAccess.Repositories;

public sealed class SqlRecommendationRepository(IDbContextFactory<AiDbContext> factory)
    : IRecommendationRepository
{
    public async Task<List<AiRecommendation>> GetPendingAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.AIRecommendations
            .Where(r => !r.IsExecuted && (r.OperatorApproved == true || r.AutoFixAvailable))
            .ToListAsync(ct);
    }

    public async Task SaveAsync(AiRecommendation recommendation, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.AIRecommendations.Add(recommendation);
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkExecutedAsync(int recommendationId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rec = await db.AIRecommendations.FindAsync([recommendationId], ct);
        if (rec is null) return;
        rec.IsExecuted = true;
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> SetApprovalAsync(int recommendationId, bool approved, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.AIRecommendations
            .Where(r => r.RecommendationId == recommendationId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.OperatorApproved, approved), ct);
        return rows > 0;
    }

    public async Task<PagedResult<AiRecommendation>> GetPagedAsync(
        int page, int pageSize, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var query = db.AIRecommendations
            .Include(r => r.Failure)
            .Include(r => r.ErrorType)
            .OrderByDescending(r => r.RecommendedAt);

        var total = await query.CountAsync(ct);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<AiRecommendation>(items, total, page, pageSize);
    }
}
