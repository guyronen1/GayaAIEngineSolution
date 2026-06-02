using MaiaAI.Core.Entities;

namespace MaiaAI.Core.Interfaces;

/// <summary>
/// What happened when the fix engine processed a recommendation. Caller
/// (ExecuteFixesUseCase) maps these to the right JobStatus transition:
///
///   Success            → JobStatus.Resolved
///   NoAutomatedAction  → if operator-approved: JobStatus.AwaitingManualAction,
///                        otherwise (auto-heal path): JobStatus.ManualRequired
///   Failed             → JobStatus.ManualRequired
///
/// Distinguishing NoAutomatedAction from Failed lets the audit trail and
/// status pipeline say "operator must perform action off-system" vs "the
/// system tried and the fix didn't work" — two genuinely different states.
/// </summary>
public enum FixOutcome
{
    /// <summary>The executor ran and reported success.</summary>
    Success,
    /// <summary>The matched policy is ActionType=Manual (or the fallback handler
    /// is the Manual category handler) — no automated step exists. Not a failure;
    /// the operator's approval IS the action.</summary>
    NoAutomatedAction,
    /// <summary>An automated step was attempted but did not succeed.</summary>
    Failed,
}

/// <summary>
/// Executes a concrete remediation action for a recommendation.
/// Each FixCategory dispatches to a different implementation strategy.
/// </summary>
public interface IFixEngine
{
    Task<FixOutcome> ExecuteAsync(AiRecommendation recommendation, CancellationToken ct = default);
}
