using MaiaAI.Core.Interfaces;
using MaiaAI.Core.Analysis;
using MaiaAI.Infrastructure.Analysis;
using MaiaAI.Infrastructure.Classification;
using MaiaAI.Infrastructure.DataAccess;
using MaiaAI.Infrastructure.DataAccess.Repositories;
using MaiaAI.Infrastructure.Fix;
using MaiaAI.Infrastructure.Parsing;
using MaiaAI.Infrastructure.Placeholders;
using MaiaAI.Infrastructure.Scanning;
using MaiaAI.Infrastructure.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MaiaAI.Infrastructure.Extensions;

/// <summary>
/// Registers all Infrastructure services: database, repositories, strategies, parsers, workers.
/// Use case (Application layer) registrations live in AIEngineAPI.Extensions.ServiceRegistration
/// so Infrastructure has no upward dependency on Application.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMaiaAI(
        this IServiceCollection services,
        string connectionString)
    {
        // ── Database ────────────────────────────────────────────────────────
        services.AddDbContextFactory<AiDbContext>(opts =>
            opts.UseSqlServer(connectionString));

        // ── Repositories ────────────────────────────────────────────────────
        services.AddScoped<IJobRepository,                SqlJobRepository>();
        services.AddScoped<IRecommendationRepository,     SqlRecommendationRepository>();
        services.AddScoped<IFixLogRepository,             SqlFixLogRepository>();
        services.AddScoped<IAuditRepository,              SqlAuditRepository>();
        services.AddScoped<IClassificationRuleRepository, SqlClassificationRuleRepository>();
        services.AddScoped<IMonitoredJobRepository,       SqlMonitoredJobRepository>();
        services.AddScoped<IFixCatalogueRepository,       SqlFixCatalogueRepository>();
        services.AddScoped<IFixPolicyRepository,          SqlFixPolicyRepository>();
        services.AddScoped<IScanWatermarkRepository,      SqlScanWatermarkRepository>();
        services.AddScoped<IMonitoredJobLeaseRepository,  SqlMonitoredJobLeaseRepository>();
        services.AddScoped<IOperatorActionRepository,     SqlOperatorActionRepository>();
        services.AddScoped<IScanRunHistoryRepository,     SqlScanRunHistoryRepository>();

        // ── Classification strategy (swap for ML/LLM here) ──────────────────
        services.AddScoped<IClassificationStrategy, RuleBasedClassifier>();

        // ── Unconfigured-failure cluster analyzer (v2 seam: add embedding/LLM
        //    implementations here and the /unconfigured controller swaps them). ─
        services.AddScoped<IUnconfiguredClusterAnalyzer, NgramClusterAnalyzer>();

        // ── Fix catalogue: DB-driven with built-in fallback ─────────────────
        services.AddScoped<IFixCatalogue, DbFixCatalogue>();

        // ── Fix engine: strategy dispatcher ─────────────────────────────────
        services.AddScoped<IFixEngine, DefaultFixEngine>();

        // ── Fix handlers: FixCategory fallback (Open/Closed) ────────────────
        services.AddScoped<IFixHandler, RetryFixHandler>();
        services.AddScoped<IFixHandler, FileRepairFixHandler>();
        services.AddScoped<IFixHandler, DbFixHandler>();
        services.AddScoped<IFixHandler, ManualFixHandler>();

        // ── Placeholder substitution (used by every executor) ────────────────
        services.AddScoped<IPlaceholderResolver, PlaceholderResolver>();

        // ── Fix action executors: one per FixActionType ──────────────────────
        services.AddScoped<IFixActionExecutor, ApiCallExecutor>();
        services.AddScoped<IFixActionExecutor, StoredProcedureExecutor>();
        services.AddScoped<IFixActionExecutor, ScriptExecutor>();
        services.AddScoped<IFixActionExecutor, SqlScriptExecutor>();
        services.AddScoped<IFixActionExecutor, ManualActionExecutor>();
        services.AddScoped<IFixActionExecutor, CopyFileExecutor>();
        // Composite is orchestrated inline by DefaultFixEngine — no separate
        // executor class. Engine iterates policy.Steps, dispatches each step
        // to its single-action executor, and writes per-step FixExecutionLog.

        // ── Parsing & I/O ────────────────────────────────────────────────────
        services.AddScoped<ILogParser, SimpleLogParser>();
        services.AddScoped<ILogReader, FileLogReader>();

        // ── Scan strategies (one per ScanType, resolved via IEnumerable<IScanStrategy>) ─
        services.AddScoped<IScanStrategy, FileSystemScanStrategy>();
        services.AddScoped<IScanStrategy, DatabaseScanStrategy>();
        services.AddScoped<IScanStrategy, ApiEndpointScanStrategy>();
        services.AddHttpClient();

        // ── Background workers ───────────────────────────────────────────────
        services.AddHostedService<MonitoringWorker>();
        services.AddHostedService<ScanHistoryRetentionWorker>();

        return services;
    }
}
