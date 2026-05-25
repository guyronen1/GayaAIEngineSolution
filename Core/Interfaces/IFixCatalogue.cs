using MaiaAI.Core.Results;

namespace MaiaAI.Core.Interfaces;

/// <summary>
/// Maps a (JobType, ErrorType) pair to a fix suggestion entry.
/// Default: static dictionary keyed by ErrorType only (last-resort fallback).
/// Swap in DbFixCatalogue for operator-configurable DB-driven entries with
/// JobType-aware lookup and dictionary fallback.
/// </summary>
public interface IFixCatalogue
{
    Task<FixCatalogueEntry?> GetEntryAsync(string errorTypeCode, int jobTypeId, CancellationToken ct = default);
}
