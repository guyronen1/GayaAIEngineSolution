using MaiaAI.Core.Entities;
using MaiaAI.Core.Enums;
using MaiaAI.Core.Results;

namespace MaiaAI.Core.Interfaces;

public interface IJobRepository
{
    Task<List<JobFailure>> GetByStatusAsync(JobStatus status, CancellationToken ct = default);
    Task<List<JobFailure>> GetUnclassifiedAsync(CancellationToken ct = default);
    Task<JobFailure?> GetByIdAsync(int failureId, CancellationToken ct = default);
    Task<JobFailure> SaveAsync(JobFailure job, CancellationToken ct = default);
    Task UpdateStatusAsync(int failureId, JobStatus status, CancellationToken ct = default);
    Task UpdateClassificationAsync(int failureId, ClassificationResult result, CancellationToken ct = default);
    Task<PagedResult<JobFailure>> GetPagedAsync(int page, int pageSize, CancellationToken ct = default);

    /// <summary>
    /// Returns true when a non-resolved failure already exists for this job/table/column combo,
    /// so database scans don't create duplicate failures for persistent data issues.
    /// </summary>
    Task<bool> HasOpenFailureAsync(int monitoredJobId, string sourceTable, string targetField, CancellationToken ct = default);
}
