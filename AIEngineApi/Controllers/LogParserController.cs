using AIEngineAPI.Models;
using MaiaAI.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace AIEngineAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class LogParserController(ILogParser logParser) : ControllerBase
{
    [HttpPost("parse")]
    public IActionResult ParseLog([FromBody] LogParseRequest request)
    {
        var lines = logParser.ParseLog(request.LogContent);
        return Ok(lines);
    }

    [HttpPost("extract-first")]
    public IActionResult ExtractFirstError([FromBody] LogParseRequest request)
    {
        var lines = logParser.ParseLog(request.LogContent);
        var error = logParser.ExtractFirstError(lines);
        return Ok(error);
    }
}
