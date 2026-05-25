using MaiaAI.Core.Interfaces.UseCases;
using Microsoft.AspNetCore.Mvc;

namespace AIEngineAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ClassificationController(IClassifyJobsUseCase classifyUseCase) : ControllerBase
{
    [HttpPost("classify-failures")]
    public async Task<IActionResult> ClassifyFailures(CancellationToken ct)
    {
        var results = await classifyUseCase.ExecuteAsync(ct);
        return Ok(results);
    }
}
