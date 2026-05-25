using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MaiaAI.Infrastructure.DataAccess;

/// <summary>Used by 'dotnet ef migrations' at design time only.</summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AiDbContext>
{
    public AiDbContext CreateDbContext(string[] args)
    {
        var opts = new DbContextOptionsBuilder<AiDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=AIEngineDb;Trusted_Connection=True;TrustServerCertificate=True;")
            .Options;
        return new AiDbContext(opts);
    }
}
