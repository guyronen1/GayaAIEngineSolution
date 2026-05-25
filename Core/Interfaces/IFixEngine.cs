using MaiaAI.Core.Entities;

namespace MaiaAI.Core.Interfaces;

/// <summary>
/// Executes a concrete remediation action for a recommendation.
/// Each FixCategory dispatches to a different implementation strategy.
/// </summary>
public interface IFixEngine
{
    Task<bool> ExecuteAsync(AiRecommendation recommendation, CancellationToken ct = default);
}
