using MaiaAI.Core.Entities;
using MaiaAI.Core.Enums;
using MaiaAI.Infrastructure.DataAccess;
using MaiaAI.Infrastructure.Security;
using MaiaAI.Infrastructure.Workers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AIEngineTests.Integration;

/// <summary>
/// Boots the real API pipeline (auth handler + tier policies + fallback +
/// MustChangePassword middleware) over an InMemory DB for the end-to-end
/// authorization matrix. Background workers are stripped (they'd churn against the
/// empty store). Seeds one user per role (+ one must-change admin), all sharing
/// <see cref="Password"/>.
/// </summary>
public sealed class AuthTestFactory : WebApplicationFactory<Program>
{
    public const string Password = "Test!1234";

    public const string AdminUser      = "admin";
    public const string OperatorUser   = "operator";
    public const string UserUser       = "user";
    public const string MustChangeUser = "mustchange";

    private readonly string _dbName = "authmatrix-" + Guid.NewGuid().ToString("N");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // Guarantee a connection string exists so AddMaiaAI doesn't throw, regardless
        // of whether appsettings.json is discovered from the test content root. The
        // SQL Server factory it registers is replaced with InMemory below.
        builder.ConfigureAppConfiguration(cfg => cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Server=(localdb)\\unused;Database=unused;",
        }));

        builder.ConfigureServices(services =>
        {
            // SQL Server → InMemory.
            RemoveByServiceType(services, typeof(IDbContextFactory<AiDbContext>));
            RemoveByServiceType(services, typeof(DbContextOptions<AiDbContext>));
            RemoveByServiceType(services, typeof(DbContextOptions));
            services.AddDbContextFactory<AiDbContext>(o => o.UseInMemoryDatabase(_dbName));

            // Drop background workers — irrelevant to authz and noisy against InMemory.
            RemoveByImplType(services, typeof(MonitoringWorker));
            RemoveByImplType(services, typeof(ScanHistoryRetentionWorker));
        });
    }

    /// <summary>Create the role users. Idempotent. Call from the test's InitializeAsync.</summary>
    public async Task SeedUsersAsync()
    {
        var factory = Services.GetRequiredService<IDbContextFactory<AiDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();   // applies the Roles HasData seed
        if (await db.Users.AnyAsync()) return;

        var hash = new Pbkdf2PasswordHasher().Hash(Password);
        db.Users.AddRange(
            User(AdminUser,      MaiaRole.Administrator, mustChange: false, hash),
            User(OperatorUser,   MaiaRole.Operator,      mustChange: false, hash),
            User(UserUser,       MaiaRole.User,          mustChange: false, hash),
            User(MustChangeUser, MaiaRole.Administrator, mustChange: true,  hash));
        await db.SaveChangesAsync();
    }

    private static User User(string username, MaiaRole role, bool mustChange, string hash) => new()
    {
        Username           = username,
        PasswordHash       = hash,
        RoleId             = (int)role,
        IsActive           = true,
        MustChangePassword = mustChange,
        CreatedAt          = DateTime.Now,
    };

    private static void RemoveByServiceType(IServiceCollection s, Type serviceType)
    {
        foreach (var d in s.Where(x => x.ServiceType == serviceType).ToList()) s.Remove(d);
    }

    private static void RemoveByImplType(IServiceCollection s, Type implType)
    {
        foreach (var d in s.Where(x => x.ImplementationType == implType).ToList()) s.Remove(d);
    }
}
