using MaiaAI.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace AIEngineAPI.Controllers;

/// <summary>
/// Operator-triggered maintenance endpoints. Keep this controller intentionally
/// small — anything that lives here bypasses the normal background schedules
/// and should be reserved for ops use.
/// </summary>
[ApiController]
[Route("api/admin")]
public class AdminController(IScanHistoryRetentionService retention) : ControllerBase
{
    /// <summary>
    /// Runs the ScanRunHistory retention sweep immediately. Same code path
    /// the ScanHistoryRetentionWorker invokes on its schedule. Returns the
    /// number of rows deleted and how long the sweep took.
    /// </summary>
    [HttpPost("scan-history/cleanup")]
    public async Task<IActionResult> RunScanHistoryCleanup(CancellationToken ct)
    {
        var result = await retention.SweepAsync(ct);
        return Ok(new
        {
            result.RowsDeleted,
            result.DurationMs,
            Cutoff  = result.Cutoff,
            Skipped = result.Skipped,
        });
    }
}
