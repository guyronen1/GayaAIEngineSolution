using MaiaAI.Core.Entities;
using MaiaAI.Core.Enums;
using MaiaAI.Core.Interfaces;
using MaiaAI.Core.Interfaces.UseCases;
using MaiaAI.Core.Results;
using MaiaAI.Infrastructure.Scanning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AIEngineTests.Unit;

/// <summary>
/// Unit tests for the CheckType.SqlQuery branch of DatabaseScanStrategy, using a
/// fake ISqlQueryRunner (the testability seam) so no live database is needed.
/// Pins the v1 contract: every returned row is a failure (Option A — no extra
/// predicate); TargetField read BY NAME; SourceIdColumn optional with row-index
/// fallback; EXEC/SELECT text passed verbatim; row cap; short StepName +
/// "db://conn/query" SourceLogPath; missing-TargetField hard failure; no-watermark
/// open-failure dedup. The existing ColumnRange/ValueEquals paths talk to
/// SqlConnection directly and remain untested in v1 (known, scoped gap).
/// </summary>
public class DatabaseScanStrategySqlQueryTests
{
    // ── Fakes ────────────────────────────────────────────────────────────────

    private sealed class FakeSqlRunner(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows) : ISqlQueryRunner
    {
        public string? LastCommandText { get; private set; }
        public int      LastMaxRows    { get; private set; }

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(
            string connectionString, string commandText, int maxRows, CancellationToken ct = default)
        {
            LastCommandText = commandText;
            LastMaxRows     = maxRows;
            return Task.FromResult(rows);
        }
    }

    private sealed class Harness
    {
        public readonly List<JobFailure> Saved = new();
        public bool OpenFailureExists { get; init; }

        public DatabaseScanStrategy Build(ISqlQueryRunner runner)
        {
            var jobRepo = new Mock<IJobRepository>();
            var next = 1;
            jobRepo.Setup(r => r.SaveAsync(It.IsAny<JobFailure>(), It.IsAny<CancellationToken>()))
                   .Returns((JobFailure f, CancellationToken _) => { f.FailureId = next++; Saved.Add(f); return Task.FromResult(f); });
            jobRepo.Setup(r => r.HasOpenFailureAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(OpenFailureExists);

            var classify = new Mock<IClassifyJobsUseCase>();
            classify.Setup(c => c.ExecuteAsync(It.IsAny<IEnumerable<JobFailure>>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((IReadOnlyList<ClassificationResult>)Array.Empty<ClassificationResult>());

            var suggest = new Mock<IGenerateSuggestionsUseCase>();
            suggest.Setup(s => s.ExecuteAsync(It.IsAny<IEnumerable<ClassificationResult>>(), It.IsAny<CancellationToken>()))
                   .Returns(Task.CompletedTask);

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:TestDb"] = "Server=fake;Database=x;" })
                .Build();

            return new DatabaseScanStrategy(
                config, jobRepo.Object, new Mock<IScanWatermarkRepository>().Object,
                classify.Object, suggest.Object, runner,
                NullLogger<DatabaseScanStrategy>.Instance);
        }
    }

    // ── Builders ─────────────────────────────────────────────────────────────

    private static IReadOnlyDictionary<string, object?> Row(params (string Name, object? Value)[] cols)
        => cols.ToDictionary(c => c.Name, c => c.Value, StringComparer.OrdinalIgnoreCase);

    private static ScanCheckRule SqlRule(int id, string query, string targetField,
        string? sourceIdColumn = null, string? desc = null)
        => new()
        {
            CheckRuleId    = id,
            MonitoredJobId = 1,
            ScanSourceId   = 1,
            CheckType      = CheckType.SqlQuery,
            SourceTable    = query,
            TargetField    = targetField,
            SourceIdColumn = sourceIdColumn,
            Description    = desc,
            IsActive       = true,
        };

    private static (MonitoredJob Job, ScanSource Source) JobAndSource(params ScanCheckRule[] rules)
        => (new MonitoredJob { MonitoredJobId = 1, JobTypeId = 7, Name = "TestJob" },
            new ScanSource { ScanSourceId = 1, MonitoredJobId = 1, Name = "DB", ConnectionName = "TestDb", ScanCheckRules = rules.ToList() });

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RawSelect_CreatesFailurePerRow_WithSourceIdMessageAndPaths()
    {
        var runner = new FakeSqlRunner(new[]
        {
            Row(("OrderId", "ORD-1"), ("IsStuck", 1)),
            Row(("OrderId", "ORD-2"), ("IsStuck", 1)),
        });
        var h = new Harness();
        var strat = h.Build(runner);
        var (job, source) = JobAndSource(SqlRule(10,
            "SELECT OrderId, IsStuck FROM Orders WHERE IsStuck = 1", "IsStuck",
            sourceIdColumn: "OrderId", desc: "Stuck orders"));

        var result = await strat.ScanAsync(job, source);

        Assert.Equal(2, result.FailuresDetected);
        Assert.Equal(new[] { "ORD-1", "ORD-2" }, h.Saved.Select(f => f.SourceId));
        Assert.All(h.Saved, f => Assert.Equal("Stuck orders", f.StepName));         // Description → StepName
        Assert.All(h.Saved, f => Assert.Equal("db://TestDb/query", f.SourceLogPath));
        Assert.Contains("Stuck orders: [IsStuck] = 1", h.Saved[0].ErrorMessage);
        Assert.Contains("OrderId=ORD-1", h.Saved[0].ErrorMessage);
    }

    [Fact]
    public async Task ExecStoredProc_PassesCommandTextVerbatim_AndCap()
    {
        var runner = new FakeSqlRunner(new[] { Row(("Status", "ERROR"), ("Id", 42)) });
        var h = new Harness();
        var strat = h.Build(runner);
        const string proc = "EXEC sp_CheckStuckOrders @threshold=60";
        var (job, source) = JobAndSource(SqlRule(11, proc, "Status", sourceIdColumn: "Id"));

        await strat.ScanAsync(job, source);

        Assert.Equal(proc, runner.LastCommandText);   // run verbatim — no EXEC detection/munging
        Assert.Equal(500, runner.LastMaxRows);          // code-side cap passed to the runner
        Assert.Equal("42", Assert.Single(h.Saved).SourceId);
    }

    [Fact]
    public async Task MissingTargetFieldColumn_ThrowsClearError_NoFailures()
    {
        var runner = new FakeSqlRunner(new[] { Row(("OrderId", "ORD-1")) });   // no "IsStuck"
        var h = new Harness();
        var strat = h.Build(runner);
        var (job, source) = JobAndSource(SqlRule(12, "SELECT OrderId FROM Orders", "IsStuck"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => strat.ScanAsync(job, source));
        Assert.Contains("IsStuck", ex.Message);
        Assert.Contains("OrderId", ex.Message);   // lists the columns actually returned
        Assert.Empty(h.Saved);
    }

    [Fact]
    public async Task SourceIdColumnAbsentFromResult_FallsBackToRowIndex()
    {
        var runner = new FakeSqlRunner(new[] { Row(("Status", "ERROR")), Row(("Status", "ERROR")) });
        var h = new Harness();
        var strat = h.Build(runner);
        // SourceIdColumn configured but not present in the result set.
        var (job, source) = JobAndSource(SqlRule(13, "SELECT Status FROM X", "Status", sourceIdColumn: "MissingId"));

        await strat.ScanAsync(job, source);

        Assert.Equal(new[] { "1", "2" }, h.Saved.Select(f => f.SourceId));
    }

    [Fact]
    public async Task NoSourceIdColumnConfigured_UsesRowIndex()
    {
        var runner = new FakeSqlRunner(new[] { Row(("V", "x")) });
        var h = new Harness();
        var strat = h.Build(runner);
        var (job, source) = JobAndSource(SqlRule(14, "SELECT V FROM X", "V"));

        await strat.ScanAsync(job, source);

        Assert.Equal("1", Assert.Single(h.Saved).SourceId);
    }

    [Fact]
    public async Task HitsRowCap_DoesNotThrow_AndCreatesAllReturnedRows()
    {
        var rows = Enumerable.Range(1, 500)
            .Select(i => Row(("V", i), ("Id", i)))
            .ToArray<IReadOnlyDictionary<string, object?>>();
        var runner = new FakeSqlRunner(rows);
        var h = new Harness();
        var strat = h.Build(runner);
        var (job, source) = JobAndSource(SqlRule(15, "SELECT V, Id FROM Big", "V", sourceIdColumn: "Id"));

        var result = await strat.ScanAsync(job, source);

        Assert.Equal(500, result.FailuresDetected);
        Assert.Equal(500, runner.LastMaxRows);
    }

    [Fact]
    public async Task NoDescription_StepNameIsPerRuleLabel()
    {
        var runner = new FakeSqlRunner(new[] { Row(("V", "x"), ("Id", "k")) });
        var h = new Harness();
        var strat = h.Build(runner);
        var (job, source) = JobAndSource(SqlRule(99, "SELECT V, Id FROM X", "V", sourceIdColumn: "Id"));

        await strat.ScanAsync(job, source);

        Assert.Equal("SqlQuery #99", Assert.Single(h.Saved).StepName);
    }

    [Fact]
    public async Task NoRows_NoFailures()
    {
        var runner = new FakeSqlRunner(Array.Empty<IReadOnlyDictionary<string, object?>>());
        var h = new Harness();
        var strat = h.Build(runner);
        var (job, source) = JobAndSource(SqlRule(16, "SELECT V FROM X WHERE 1=0", "V"));

        var result = await strat.ScanAsync(job, source);

        Assert.Equal(0, result.FailuresDetected);
        Assert.Empty(h.Saved);
    }

    [Fact]
    public async Task OpenFailureExists_DedupSkipsCreation()
    {
        var runner = new FakeSqlRunner(new[] { Row(("V", "x"), ("Id", "k")) });
        var h = new Harness { OpenFailureExists = true };
        var strat = h.Build(runner);
        var (job, source) = JobAndSource(SqlRule(17, "SELECT V, Id FROM X", "V", sourceIdColumn: "Id", desc: "D"));

        var result = await strat.ScanAsync(job, source);

        Assert.Equal(0, result.FailuresDetected);
        Assert.Empty(h.Saved);
    }
}
