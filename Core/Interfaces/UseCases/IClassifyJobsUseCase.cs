using MaiaAI.Core.Entities;
using MaiaAI.Core.Results;

namespace MaiaAI.Core.Interfaces.UseCases;

public interface IClassifyJobsUseCase
{
    Task<IReadOnlyList<ClassificationResult>> ExecuteAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ClassificationResult>> ExecuteAsync(IEnumerable<JobFailure> jobList, CancellationToken ct = default);
}
