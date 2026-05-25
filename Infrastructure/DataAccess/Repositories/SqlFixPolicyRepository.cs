using MaiaAI.Core.Entities;
using MaiaAI.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace MaiaAI.Infrastructure.DataAccess.Repositories;

/// <summary>
/// Loads the full FixPolicyRule (including ActionType + ActionPayload) for execution.
/// </summary>
public sealed class SqlFixPolicyRepository(IDbContextFactory<AiDbContext> factory)
    : IFixPolicyRepository
{
    public async Task<FixPolicyRule?> GetForAsync(
        int jobTypeId,
        int errorTypeId,
        CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.FixPolicyRules
            .Include(r => r.ErrorType)
            .Where(r => r.JobTypeId   == jobTypeId
                     && r.ErrorTypeId == errorTypeId
                     && r.Enabled)
            .OrderByDescending(r => r.ActionTimestamp)
            .FirstOrDefaultAsync(ct);
    }
}
