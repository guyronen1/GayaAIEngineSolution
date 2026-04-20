using System;
using System.Linq;
using System.Threading.Tasks;
using Shared.Interfaces;
using Shared.Models;

namespace Shared.Implementations
{
    public class AIRecommendationGenerator : IErrorClassifier
    {
        private readonly IDataRepository _repository;
        private readonly ILogParser _parser;

        public AIRecommendationGenerator(IDataRepository repository, ILogParser parser)
        {
            _repository = repository;
            _parser = parser;
        }

        public async Task ScanAndClassifyFailuresAsync()
        {
            var failedJobs = _repository.GetFailedJobs();
            foreach (var job in failedJobs)
            {
                var content = System.IO.File.Exists(job.LogPath) ? System.IO.File.ReadAllText(job.LogPath) : "";
                var lines = _parser.ParseLog(content);
                var error = lines.FirstOrDefault(l => l.Contains("ERROR") || l.Contains("Exception"));
                if (error != null)
                {
                    var recommendation = new JobRecommendation
                    {
                        JobId = job.JobId,
                        Recommendation = "Retry job",
                        Reason = $"Detected error: {error}"
                    };
                    _repository.SaveRecommendation(recommendation);
                }
            }

            await Task.CompletedTask;
        }
    }
}
