using MaiaAI.Core.Entities;
using MaiaAI.Core.Enums;
using MaiaAI.Core.Results;

namespace MaiaAI.Core.Interfaces;

public interface IScanRunHistoryRepository
{
    Task SaveAsync(ScanRunHistory run, CancellationToken ct = default);

    Task<PagedResult<ScanRunHistory>> GetPagedAsync(
        int?           monitoredJobId,
        JobRunOutcome? outcome,
        DateTime?      fromDate,
        DateTime?      toDate,
        int            page,
        int            pageSize,
        CancellationToken ct = default);

    /// <summary>
    /// Bounded DELETE — removes up to <paramref name="batchSize"/> rows whose
    /// <see cref="ScanRunHistory.CompletedAt"/> is older than <paramref name="cutoff"/>.
    /// Returns the number of rows actually deleted; caller loops until 0.
    /// </summary>
    Task<int> DeleteOlderThanAsync(DateTime cutoff, int batchSize, CancellationToken ct = default);
}
