using Microsoft.EntityFrameworkCore;
using Shared.Models;

namespace Shared.DataAccess
{
    public class AiDbContext : DbContext
    {
        public AiDbContext(DbContextOptions<AiDbContext> options) : base(options) { }

        public DbSet<JobRecord> JobRecords { get; set; }
        public DbSet<JobRecommendation> Recommendations { get; set; }
    }
}
