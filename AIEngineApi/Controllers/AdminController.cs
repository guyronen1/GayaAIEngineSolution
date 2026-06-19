using MaiaAI.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AIEngineAPI.Controllers;

/// <summary>
/// Operator-triggered maintenance endpoints. Keep this controller intentionally
/// small — anything that lives here bypasses the normal background schedules
/// and should be reserved for ops use.
/// </summary>
[ApiController]
[Route("api/admin")]
[Authorize(Policy = "RequireAdmin")]   // destructive/operational maintenance
public class AdminController(
    IScanHistoryRetentionService retention,
    IWorkerControlService        workerControl) : ControllerBase
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

    /// <summary>
    /// Pauses the MonitoringWorker scan loop. In-flight scans complete
    /// normally; no new claims are made until resumed.
    /// </summary>
    [HttpPost("worker/pause")]
    public IActionResult PauseWorker()
    {
        workerControl.Pause();
        return Ok(new { isPaused = true });
    }

    /// <summary>Resumes the MonitoringWorker scan loop.</summary>
    [HttpPost("worker/resume")]
    public IActionResult ResumeWorker()
    {
        workerControl.Resume();
        return Ok(new { isPaused = false });
    }
}
