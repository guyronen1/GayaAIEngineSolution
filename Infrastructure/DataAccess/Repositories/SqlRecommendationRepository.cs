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
        // Include Failure so DefaultFixEngine can read recommendation.Failure.JobTypeId
        // for the (JobTypeId + ErrorTypeId) policy lookup without an extra round-trip.
        return await db.AIRecommendations
            .Include(r => r.Failure)
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

    public async Task<bool> ExistsForFailureAsync(int failureId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.AIRecommendations.AnyAsync(r => r.FailureId == failureId, ct);
    }

    public async Task<PagedResult<RecommendationListItem>> GetPagedAsync(
        int page, int pageSize, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var query = db.AIRecommendations
            .Include(r => r.Failure)
            .Include(r => r.ErrorType)
            .OrderByDescending(r => r.RecommendedAt);

        var total = await query.CountAsync(ct);

        // Correlated subquery: pick the newest enabled FixPolicyRule for this rec's
        // (JobTypeId + ErrorTypeId) pair. Mirrors DefaultFixEngine / SqlFixPolicyRepository
        // semantics exactly — same filter, same tiebreaker — so the UI shows the policy
        // that will actually be used at execution time.
        var raw = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new
            {
                Rec    = r,
                Policy = db.FixPolicyRules
                    .Where(p => p.Enabled
                             && p.ErrorTypeId == r.ErrorTypeId
                             && p.JobTypeId   == r.Failure!.JobTypeId)
                    .OrderByDescending(p => p.ActionTimestamp)
                    .FirstOrDefault()
            })
            .ToListAsync(ct);

        var items = raw
            .Select(x => new RecommendationListItem(
                x.Rec,
                x.Policy?.RuleId,
                x.Policy?.IsAutoHealEligible))
            .ToList();

        return new PagedResult<RecommendationListItem>(items, total, page, pageSize);
    }
}
