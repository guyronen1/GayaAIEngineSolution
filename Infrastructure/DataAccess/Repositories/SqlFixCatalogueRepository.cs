using MaiaAI.Core.Interfaces;
using MaiaAI.Core.Results;
using Microsoft.EntityFrameworkCore;

namespace MaiaAI.Infrastructure.DataAccess.Repositories;

/// <summary>
/// Reads fix entries from the FixPolicyRules table so operators can configure
/// fix actions at runtime without code changes.
/// </summary>
public sealed class SqlFixCatalogueRepository(IDbContextFactory<AiDbContext> factory)
    : IFixCatalogueRepository
{
    public async Task<FixCatalogueEntry?> GetEntryAsync(
        string errorTypeCode,
        int    jobTypeId,
        CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var rule = await db.FixPolicyRules
            .Include(r => r.ErrorType)
            .Where(r => r.Enabled
                     && r.JobTypeId == jobTypeId
                     && r.ErrorType != null
                     && r.ErrorType.Code == errorTypeCode)
            .OrderByDescending(r => r.ActionTimestamp)
            .FirstOrDefaultAsync(ct);

        if (rule is null) return null;

        return new FixCatalogueEntry(
            rule.ActionToApply,
            rule.FixCategory,
            ConfidenceBoost: 0.0,
            rule.IsAutoHealEligible);
    }
}
