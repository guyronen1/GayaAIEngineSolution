using MaiaAI.Core.Entities;

namespace MaiaAI.Core.Interfaces;

/// <summary>
/// Fetches the full FixPolicyRule (including ActionType + ActionPayload) for execution.
/// Separate from IFixCatalogueRepository which serves suggestion generation.
/// Filters by (JobTypeId + ErrorTypeId) — both required; non-nullable in the schema.
/// </summary>
public interface IFixPolicyRepository
{
    Task<FixPolicyRule?> GetForAsync(int jobTypeId, int errorTypeId, CancellationToken ct = default);
}
