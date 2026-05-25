using AIEngineAPI.Contracts;
using MaiaAI.Core.Entities;
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
/// records the decision without triggering execution.
///
/// Both endpoints record an <see cref="OperatorAction"/> and an <see cref="AuditLog"/> entry.
/// </summary>
[ApiController]
[Route("api/recommendations")]
public class RecommendationsController(
    IRecommendationRepository       recommendations,
    IOperatorActionRepository       operatorActions,
    IAuditRepository                audit,
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
            FailureId = rec.FailureId,
            EventType = approved ? "OperatorApproved" : "OperatorRejected",
            Actor     = req.OperatorId,
            Detail    = $"Operator {req.OperatorId} {actionTaken.ToLowerInvariant()}d recommendation {id} " +
                        $"(action: {rec.SuggestedAction}).",
            Timestamp = DateTime.Now,
        }, ct);

        if (approved)
            await execute.ExecuteAsync(ct);

        rec.OperatorApproved = approved;
        return Ok(RecommendationDto.From(rec));
    }
}
