using MaiaAI.Core.Enums;

namespace MaiaAI.Core.Results;

public sealed record FixCatalogueEntry(
    string SuggestedAction,
    FixCategory Category,
    double ConfidenceBoost,
    bool AutoHeal);
