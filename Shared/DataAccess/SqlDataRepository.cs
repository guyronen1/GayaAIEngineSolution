using System.Collections.Generic;
using System.Linq;
using Shared.Interfaces;
using Shared.Models;

namespace Shared.DataAccess
{
    public class SqlDataRepository : IDataRepository
    {
        private readonly AiDbContext _context;

        public SqlDataRepository(AiDbContext context)
        {
            _context = context;
        }

        public List<JobRecord> GetFailedJobs()
        {
            return _context.JobRecords.Where(j => j.Status == "Failed").ToList();
        }

        public void SaveRecommendation(JobRecommendation recommendation)
        {
            _context.Recommendations.Add(recommendation);
            _context.SaveChanges();
        }
    }
}
