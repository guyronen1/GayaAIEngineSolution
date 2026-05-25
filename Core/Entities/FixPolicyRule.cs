using MaiaAI.Core.Enums;

namespace MaiaAI.Core.Entities;

public class FixPolicyRule
{
    public int RuleId { get; set; }
    public int JobTypeId { get; set; }
    public int ErrorTypeId { get; set; }

    /// <summary>Human-readable description of the fix (shown to operators).</summary>
    public required string ActionToApply { get; set; }

    public FixCategory FixCategory { get; set; }
    public bool IsAutoHealEligible { get; set; }
    public bool Enabled { get; set; } = true;
    public string? CreatedBy { get; set; }
    public DateTime ActionTimestamp { get; set; }

    // ── Execution wiring ─────────────────────────────────────────────────────

    /// <summary>How to execute the fix automatically.</summary>
    public FixActionType ActionType { get; set; } = FixActionType.Manual;

    /// <summary>
    /// The action target, interpreted by the executor for this ActionType:
    /// - ApiCall:         URL (e.g. http://jobs.internal/api/retry/{failureId})
    /// - StoredProcedure: "SpName" or "ConnectionName|SpName"
    /// - Script:          executable + args (e.g. powershell.exe C:\scripts\fix.ps1 {failureId})
    /// - Manual:          null (not used)
    /// Supports {failureId} placeholder substitution at runtime.
    /// </summary>
    public string? ActionPayload { get; set; }

    public JobType? JobType { get; set; }
    public ErrorType? ErrorType { get; set; }
}
