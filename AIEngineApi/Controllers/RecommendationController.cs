using Microsoft.AspNetCore.Mvc;
using Shared.Interfaces;
using Shared.Models;

namespace AIEngineApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class RecommendationController : ControllerBase
    {
        private readonly IAIRecommendationService _recommendationService;

        public RecommendationController(IAIRecommendationService recommendationService)
        {
            _recommendationService = recommendationService;
        }

        [HttpGet("{jobId}")]
        public ActionResult<RecommendationResult> Get(int jobId)
        {
            var result = _recommendationService.AnalyzeAndSuggest(jobId);
            if (result == null)
                return NotFound();

            return Ok(result);
        }
    }
}
