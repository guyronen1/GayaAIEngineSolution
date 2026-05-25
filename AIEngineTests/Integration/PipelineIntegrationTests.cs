using MaiaAI.Application.Classification;
using MaiaAI.Application.Remediation;
using MaiaAI.Core.Entities;
using MaiaAI.Core.Enums;
using MaiaAI.Infrastructure.Classification;
using MaiaAI.Infrastructure.DataAccess;
using MaiaAI.Infrastructure.DataAccess.Repositories;
using MaiaAI.Infrastructure.Parsing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AIEngineTests.Integration;

/// <summary>
/// End-to-end pipeline tests using EF Core InMemory.
///
/// Trade-off: InMemory does not enforce FK constraints or SQL Server DDL behaviour.
/// For production-grade parity use Testcontainers.MsSql (docker-based SQL Server).
/// That upgrade requires no code changes here — swap the DbContextOptions in InitializeAsync.
/// </summary>
public class PipelineIntegrationTests : IAsyncLifetime
{
    private AiDbContext _db = null!;
    private IDbContextFactory<AiDbContext> _factory = null!;
    private string _tempLogFile = null!;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<AiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _db      = new AiDbContext(options);
        await _db.Database.EnsureCreatedAsync();

        _factory     = new TestDbContextFactory(options);
        _tempLogFile = Path.GetTempFileName();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        if (File.Exists(_tempLogFile)) File.Delete(_tempLogFile);
    }

    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FullPipeline_FailedJob_ProducesRecommendation()
    {
        // Pattern matches seeded DTSX rule: DTS_E_CANNOTACQUIRECONNECTION → DbConnection → Retry
        await File.WriteAllTextAsync(_tempLogFile,
            "Starting DTSX job\nDTS_E_CANNOTACQUIRECONNECTION: Cannot acquire connection\nJob aborted");

        _db.JobFailures.Add(new JobFailure
        {
            JobId = 1, JobTypeId = 1,
            Status = JobStatus.Failed, SourceLogPath = _tempLogFile,
        });
        await _db.SaveChangesAsync();

        var (classifier, suggestionSvc) = BuildPipeline();

        var results = await classifier.ExecuteAsync();
        await suggestionSvc.ExecuteAsync(results);

        var recommendations = await _db.AIRecommendations.ToListAsync();
        Assert.Single(recommendations);

        var rec = recommendations[0];
        Assert.Equal(1, rec.FailureId);
        Assert.Equal(FixCategory.Retry, rec.FixCategory);
        Assert.True(rec.AutoFixAvailable);
        Assert.True(rec.ConfidenceScore > 0.5m);
    }

    [Fact]
    public async Task FullPipeline_CleanLog_ProducesNoRecommendation()
    {
        await File.WriteAllTextAsync(_tempLogFile,
            "Starting DTSX job\nAll rows processed\nCompleted successfully");

        _db.JobFailures.Add(new JobFailure
        {
            JobId = 2, JobTypeId = 1,
            Status = JobStatus.Failed, SourceLogPath = _tempLogFile,
        });
        await _db.SaveChangesAsync();

        var (classifier, suggestionSvc) = BuildPipeline();

        var results = await classifier.ExecuteAsync();
        await suggestionSvc.ExecuteAsync(results);

        Assert.Empty(await _db.AIRecommendations.ToListAsync());
    }

    // ─────────────────────────────────────────────────────────────────────────

    private (ClassifyJobsUseCase, GenerateSuggestionsUseCase) BuildPipeline()
    {
        var jobRepo    = new SqlJobRepository(_factory);
        var ruleRepo   = new SqlClassificationRuleRepository(_factory);
        var monJobRepo = new SqlMonitoredJobRepository(_factory);
        var recRepo    = new SqlRecommendationRepository(_factory);
        var parser     = new SimpleLogParser();
        var logReader  = new FileLogReader(NullLogger<FileLogReader>.Instance);
        var strategy   = new RuleBasedClassifier(ruleRepo, monJobRepo, parser);
        var catalogue  = new FixCatalogue();

        var classifier = new ClassifyJobsUseCase(
            jobRepo, strategy, logReader,
            NullLogger<ClassifyJobsUseCase>.Instance);

        var suggestionSvc = new GenerateSuggestionsUseCase(
            recRepo, catalogue,
            NullLogger<GenerateSuggestionsUseCase>.Instance);

        return (classifier, suggestionSvc);
    }

    // ── Minimal factory shim ─────────────────────────────────────────────────

    private sealed class TestDbContextFactory(DbContextOptions<AiDbContext> options)
        : IDbContextFactory<AiDbContext>
    {
        public AiDbContext CreateDbContext() => new(options);
    }
}
