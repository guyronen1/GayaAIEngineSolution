using MaiaAI.Core.Entities;
using MaiaAI.Core.Enums;
using MaiaAI.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace AIEngineAPI.Controllers;

/// <summary>
/// Operator actions on JobFailures. Sibling to RecommendationsController:
/// that one handles operator decisions on recommendations (approve/reject);
/// this one handles operator actions on the failure itself (mark resolved,
/// future: defer / reopen).
///
/// "Mark Resolved" is the exit ramp for failures that landed in
/// <see cref="JobStatus.AwaitingManualAction"/> — the operator approved a
/// Manual-action recommendation, performed the work off-system, and is now
/// confirming it. Also usable on any other failure status (e.g. operator
/// resolves a Failed/ManualRequired directly without going through a
/// recommendation), so the endpoint doesn't gate on current status.
/// </summary>
[ApiController]
[Route("api/failures")]
public class FailuresController(
    IJobRepository    jobs,
    IAuditRepository  audit) : ControllerBase
{
    public sealed record MarkResolvedRequest(string OperatorId);

    [HttpPost("{id:int}/mark-resolved")]
    public async Task<IActionResult> MarkResolved(
        int id, [FromBody] MarkResolvedRequest req, CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.OperatorId))
            return BadRequest(new { Message = "operatorId is required." });

        // Fetch first so we can record the prior status in the audit detail
        // (lets an auditor see "was AwaitingManualAction → Resolved" vs
        // "was Failed → Resolved" without joining other tables).
        var failure = await jobs.GetByIdAsync(id, ct);
        if (failure is null)
            return NotFound(new { Message = $"JobFailure {id} not found." });

        var priorStatus = failure.Status;
        if (priorStatus == JobStatus.Resolved)
            // Idempotent — re-marking an already-resolved failure is a no-op,
            // not an error. The audit row would just be noise.
            return NoContent();

        await jobs.UpdateStatusAsync(id, JobStatus.Resolved, ct);

        await audit.WriteAsync(new AuditLog
        {
            FailureId  = id,
            EntityType = "JobFailure",
            EntityId   = id.ToString(),
            EventType  = "ManuallyResolved",
            Actor      = req.OperatorId,
            Detail     = $"Operator {req.OperatorId} marked failure {id} as resolved (was {priorStatus}).",
            Timestamp  = DateTime.Now,
        }, ct);

        return NoContent();
    }
}
