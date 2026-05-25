using MaiaAI.Core.Entities;
using MaiaAI.Core.Results;

namespace MaiaAI.Core.Interfaces;

public interface IRecommendationRepository
{
    Task<List<AiRecommendation>> GetPendingAsync(CancellationToken ct = default);
    Task SaveAsync(AiRecommendation recommendation, CancellationToken ct = default);
    Task MarkExecutedAsync(int recommendationId, CancellationToken ct = default);
    Task<PagedResult<RecommendationListItem>> GetPagedAsync(int page, int pageSize, CancellationToken ct = default);
    Task<bool> ExistsForFailureAsync(int failureId, CancellationToken ct = default);

    /// <summary>
    /// Sets the operator decision on a recommendation. <c>true</c> = approved (eligible for
    /// execution by <see cref="UseCases.IExecuteFixesUseCase"/>), <c>false</c> = rejected.
    /// Returns <c>true</c> if a row was updated, <c>false</c> if no recommendation exists with that id.
    /// </summary>
    Task<bool> SetApprovalAsync(int recommendationId, bool approved, CancellationToken ct = default);
}
