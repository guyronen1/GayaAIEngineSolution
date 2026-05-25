using MaiaAI.Core.Entities;
using MaiaAI.Core.Enums;

namespace MaiaAI.Core.Interfaces;

/// <summary>
/// Executes a specific type of automated fix action.
/// One implementation per FixActionType; DefaultFixEngine dispatches via IFixPolicyRepository.
/// </summary>
public interface IFixActionExecutor
{
    FixActionType ActionType { get; }

    /// <param name="payload">The action payload from FixPolicyRule (URL, SP name, script command).</param>
    /// <param name="recommendation">The recommendation being executed; provides context (FailureId, ErrorTypeId).</param>
    Task<bool> ExecuteAsync(
        string? payload,
        AiRecommendation recommendation,
        CancellationToken ct = default);
}
