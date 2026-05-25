using MaiaAI.Core.Entities;
using MaiaAI.Core.Enums;
using MaiaAI.Core.Interfaces;
using MaiaAI.Infrastructure.DataAccess;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MaiaAI.Infrastructure.Fix;

/// <summary>
/// Executes a fix by running a raw SQL statement.
///
/// Connection resolution order:
///   1. ActionPayload prefix "ConnectionName|SQL" — explicit override
///   2. The failure's MonitoredJob.ConnectionName (set by Database scan jobs)
///   3. "DefaultConnection" (AIEngineDb itself)
///
/// Placeholder substitution (case-insensitive):
///   {failureId} → JobFailure.FailureId (int)
///   {sourceId}  → JobFailure.SourceId  (string; e.g. the source row's GUID/key)
/// </summary>
public sealed class SqlScriptExecutor(
    IDbContextFactory<AiDbContext> factory,
    IConfiguration                 config,
    ILogger<SqlScriptExecutor>     logger) : IFixActionExecutor
{
    public FixActionType ActionType => FixActionType.SqlScript;

    public async Task<bool> ExecuteAsync(
        string? payload,
        AiRecommendation recommendation,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            logger.LogError("SqlScriptExecutor: ActionPayload (SQL script) is required for Failure {FailureId}",
                recommendation.FailureId);
            return false;
        }

        var (connectionName, sqlTemplate) = SplitPayload(payload);

        // Look up the failure for SourceId + MonitoredJob.ConnectionName fallback
        await using var db = await factory.CreateDbContextAsync(ct);
        var failure = await db.JobFailures
            .Include(j => j.MonitoredJob)
            .FirstOrDefaultAsync(j => j.FailureId == recommendation.FailureId, ct);

        if (failure is null)
        {
            logger.LogError("SqlScriptExecutor: Failure {FailureId} not found", recommendation.FailureId);
            return false;
        }

        connectionName ??= failure.MonitoredJob?.ConnectionName ?? "DefaultConnection";
        var connStr = config.GetConnectionString(connectionName);
        if (string.IsNullOrWhiteSpace(connStr))
        {
            logger.LogError(
                "SqlScriptExecutor: Connection '{ConnectionName}' not found in configuration (Failure {FailureId})",
                connectionName, recommendation.FailureId);
            return false;
        }

        var sql = sqlTemplate
            .Replace("{failureId}", recommendation.FailureId.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{sourceId}",  failure.SourceId ?? string.Empty,    StringComparison.OrdinalIgnoreCase);

        try
        {
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, conn);
            var affected = await cmd.ExecuteNonQueryAsync(ct);

            logger.LogInformation(
                "SqlScriptExecutor: Script executed for Failure {FailureId} on '{ConnectionName}', rows affected: {Rows}",
                recommendation.FailureId, connectionName, affected);

            // Zero rows affected → SQL ran but matched nothing. Treat as failure so operator can investigate.
            return affected > 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "SqlScriptExecutor: Script failed for Failure {FailureId} on '{ConnectionName}'",
                recommendation.FailureId, connectionName);
            return false;
        }
    }

    private static (string? ConnectionName, string Sql) SplitPayload(string payload)
    {
        var pipe = payload.IndexOf('|');
        if (pipe <= 0) return (null, payload);
        var name = payload[..pipe].Trim();
        var sql  = payload[(pipe + 1)..].TrimStart();
        return (string.IsNullOrEmpty(name) ? null : name, sql);
    }
}
