using MaiaAI.Core.Interfaces.UseCases;
using Microsoft.AspNetCore.Mvc;

namespace AIEngineAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class FixController(
    IClassifyJobsUseCase        classify,
    IGenerateSuggestionsUseCase suggest,
    IExecuteFixesUseCase        execute) : ControllerBase
{
    [HttpPost("generate-suggestions")]
    public async Task<IActionResult> GenerateSuggestions(CancellationToken ct)
    {
        var classifications = await classify.ExecuteAsync(ct);
        await suggest.ExecuteAsync(classifications, ct);
        return Ok(new { Generated = classifications.Count });
    }

    [HttpPost("execute-fixes")]
    public async Task<IActionResult> ExecuteFixes(CancellationToken ct)
    {
        await execute.ExecuteAsync(ct);
        return Ok(new { Message = "Fixes executed" });
    }
}
