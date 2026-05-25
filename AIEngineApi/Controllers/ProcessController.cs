using MaiaAI.Core.Interfaces.UseCases;
using Microsoft.AspNetCore.Mvc;

namespace AIEngineAPI.Controllers;

/// <summary>
/// Triggers the in-database pipeline: classify all queued failures → generate suggestions → execute fixes.
/// Use /api/pipeline/run-directory for file-based ingestion.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class ProcessController(
    IClassifyJobsUseCase        classify,
    IGenerateSuggestionsUseCase suggest,
    IExecuteFixesUseCase        execute) : ControllerBase
{
    [HttpPost("run-pipeline")]
    public async Task<IActionResult> RunPipeline(CancellationToken ct)
    {
        var classifications = await classify.ExecuteAsync(ct);
        await suggest.ExecuteAsync(classifications, ct);
        await execute.ExecuteAsync(ct);

        return Ok(new { Classifications = classifications.Count, Message = "Pipeline executed" });
    }
}
