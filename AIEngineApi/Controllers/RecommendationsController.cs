using AIEngineAPI.Contracts;
using MaiaAI.Core.Entities;
using MaiaAI.Core.Enums;
using MaiaAI.Core.Interfaces;
using MaiaAI.Core.Interfaces.UseCases;
using MaiaAI.Infrastructure.DataAccess;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AIEngineAPI.Controllers;

/// <summary>
/// Operator decisions on AI recommendations. Approve flips
/// <see cref="AiRecommendation.OperatorApproved"/> to true and synchronously drains the
/// pending fix queue so the fix runs on the same request. Reject sets it to false and
/// — when no other pending recs remain on a still-Failed failure — flips the failure
/// to <see cref="JobStatus.ManualRequired"/> so the operator's decline is visible in
/// the status badge + stage pipeline (otherwise the failure would look "stuck on
/// Recommended" forever).
///
/// Both endpoints record an <see cref="OperatorAction"/> and an <see cref="AuditLog"/> entry.
/// </summary>
[ApiController]
[Route("api/recommendations")]
public class RecommendationsController(
    IRecommendationRepository       recommendations,
    IOperatorActionRepository       operatorActions,
    IAuditRepository                audit,
    IJobRepository                  jobs,
    IExecuteFixesUseCase            execute,
    IDbContextFactory<AiDbContext>  dbFactory) : ControllerBase
{
    public sealed record DecisionRequest(string OperatorId);

    [HttpPost("{id:int}/approve")]
    public Task<IActionResult> Approve(int id, [FromBody] DecisionRequest req, CancellationToken ct)
        => RecordDecisionAsync(id, approved: true, req, ct);

    [HttpPost("{id:int}/reject")]
    public Task<IActionResult> Reject(int id, [FromBody] DecisionRequest req, CancellationToken ct)
        => RecordDecisionAsync(id, approved: false, req, ct);

    private async Task<IActionResult> RecordDecisionAsync(
        int id, bool approved, DecisionRequest req, CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.OperatorId))
            return BadRequest(new { Message = "operatorId is required." });

        // Need FailureId for the audit row, so fetch before mutating
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rec = await db.AIRecommendations
            .Include(r => r.ErrorType)
            .FirstOrDefaultAsync(r => r.RecommendationId == id, ct);

        if (rec is null)
            return NotFound(new { Message = $"Recommendation {id} not found." });

        var updated = await recommendations.SetApprovalAsync(id, approved, ct);
        if (!updated)
            return NotFound(new { Message = $"Recommendation {id} not found." });

        var actionTaken = approved ? "Approve" : "Reject";
        await operatorActions.SaveAsync(new OperatorAction
        {
            RecommendationId = id,
            OperatorId       = req.OperatorId,
            ActionTaken      = actionTaken,
            ActionTimestamp  = DateTime.Now,
        }, ct);

        await audit.WriteAsync(new AuditLog
        {
            // Populate both the legacy FailureId FK and the generic
            // EntityType/EntityId discriminator so this row shows up in
            // either query path. Same shape ExecuteFixesUseCase now writes.
            FailureId  = rec.FailureId,
            EntityType = "AiRecommendation",
            EntityId   = id.ToString(),
            EventType  = approved ? "OperatorApproved" : "OperatorRejected",
            Actor      = req.OperatorId,
            Detail     = $"Operator {req.OperatorId} {actionTaken.ToLowerInvariant()}d recommendation {id} " +
                         $"(action: {rec.SuggestedAction}).",
            Timestamp  = DateTime.Now,
        }, ct);

        if (approved)
        {
            await execute.ExecuteAsync(ct);
        }
        else
        {
            // Rejection of the LAST pending rec on a Failed failure → flip
            // the failure to ManualRequired so operator's decision is visible
            // in the status badge + stage pipeline. Skip if:
            //   - another rec on the same failure is still pending (the
            //     operator hasn't decided everything)
            //   - the failure is already past the Failed state (e.g.
            //     AwaitingManualAction because a sibling rec was approved)
            await TransitionFailureIfLastRejectionAsync(rec.FailureId, id, db, req.OperatorId, ct);
        }

        rec.OperatorApproved = approved;
        return Ok(RecommendationDto.From(rec));
    }

    /// <summary>
    /// Idempotent: only flips Status when (a) failure is still Failed and
    /// (b) no recs on the failure are pending (OperatorApproved IS NULL AND
    /// IsExecuted = 0). Excludes the just-rejected rec from the pending
    /// count (it was just rejected, the SetApprovalAsync write may not have
    /// propagated to this query session depending on timing — explicit
    /// exclusion is safer than relying on read-after-write).
    /// </summary>
    private async Task TransitionFailureIfLastRejectionAsync(
        int failureId, int justRejectedId, AiDbContext db, string operatorId, CancellationToken ct)
    {
        var failure = await db.JobFailures.FirstOrDefaultAsync(f => f.FailureId == failureId, ct);
        if (failure is null || failure.Status != JobStatus.Failed) return;

        var otherPending = await db.AIRecommendations.AnyAsync(
            r => r.FailureId == failureId
              && r.RecommendationId != justRejectedId
              && r.OperatorApproved == null
              && !r.IsExecuted, ct);
        if (otherPending) return;

        await jobs.UpdateStatusAsync(failureId, JobStatus.ManualRequired, ct);

        await audit.WriteAsync(new AuditLog
        {
            FailureId  = failureId,
            EntityType = "JobFailure",
            EntityId   = failureId.ToString(),
            EventType  = "ManualActionRequired",
            Actor      = operatorId,
            Detail     = $"Operator {operatorId} rejected the last pending recommendation — failure transitioned to ManualRequired.",
            Timestamp  = DateTime.Now,
        }, ct);
    }
}
