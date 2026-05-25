using MaiaAI.Core.Results;

namespace MaiaAI.Core.Interfaces;

/// <summary>
/// Reads fix policy entries from the FixPolicyRules table.
/// Used by DbFixCatalogue to give operators runtime control over fix actions.
/// Filters by (ErrorTypeCode + JobTypeId) — both required; non-nullable in the schema.
/// </summary>
public interface IFixCatalogueRepository
{
    Task<FixCatalogueEntry?> GetEntryAsync(string errorTypeCode, int jobTypeId, CancellationToken ct = default);
}
