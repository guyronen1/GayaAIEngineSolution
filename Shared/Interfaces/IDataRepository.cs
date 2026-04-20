using System.Collections.Generic;
using Shared.Models;

namespace Shared.Interfaces
{
    public interface IDataRepository
    {
        List<JobRecord> GetFailedJobs();
        void SaveRecommendation(JobRecommendation recommendation);
    }
}
