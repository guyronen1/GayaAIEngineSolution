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
///   2. The failure's ScanSource.ConnectionName (set by Database scan sources)
///   3. "DefaultConnection" (AIEngineDb itself)
///
/// Placeholder substitution is delegated to IPlaceholderResolver — see
/// that interface for the full token list. Substitution is non-strict;
/// unresolved placeholders become empty strings (a SQL that uses
/// {sourceId} on a failure with null SourceId still runs against "").
/// </summary>
public sealed class SqlScriptExecutor(
    IDbContextFactory<AiDbContext> factory,
    IConfiguration                 config,
    IPlaceholderResolver           resolver,
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

        // Connection string fallback reads from the failure's ScanSource.ConnectionName.
        await using var db = await factory.CreateDbContextAsync(ct);
        var failure = await db.JobFailures
            .Include(j => j.ScanSource)
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.FailureId == recommendation.FailureId, ct);

        if (failure is null)
        {
            logger.LogError("SqlScriptExecutor: Failure {FailureId} not found", recommendation.FailureId);
            return false;
        }

        connectionName ??= failure.ScanSource?.ConnectionName ?? "DefaultConnection";
        var connStr = config.GetConnectionString(connectionName);
        if (string.IsNullOrWhiteSpace(connStr))
        {
            logger.LogError(
                "SqlScriptExecutor: Connection '{ConnectionName}' not found in configuration (Failure {FailureId})",
                connectionName, recommendation.FailureId);
            return false;
        }

        var sql = await resolver.ResolveAsync(sqlTemplate, recommendation, ct);

        // Hard per-step wall clock — SqlClient's default is 30s but it's the
        // *server-side* execution timeout, not a wall-clock guarantee. Pair
        // with the linked CTS so both the operator's cancel AND the per-step
        // cap can interrupt mid-query.
        using var cts = ExecutorTimeouts.LinkedWithTimeout(ct, ExecutorTimeouts.Default);

        try
        {
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync(cts.Token);
            await using var cmd = new SqlCommand(sql, conn);
            cmd.CommandTimeout = (int)ExecutorTimeouts.Default.TotalSeconds;
            var affected = await cmd.ExecuteNonQueryAsync(cts.Token);

            logger.LogInformation(
                "SqlScriptExecutor: Script executed for Failure {FailureId} on '{ConnectionName}', rows affected: {Rows}",
                recommendation.FailureId, connectionName, affected);

            // Zero rows affected → SQL ran but matched nothing. Treat as failure so operator can investigate.
            return affected > 0;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Per-step timeout fired (not the outer cancellation). Surface as
            // a distinct log so operators can grep for "timed out" patterns.
            logger.LogWarning(
                "SqlScriptExecutor: Script timed out after {Seconds}s for Failure {FailureId} on '{ConnectionName}'",
                ExecutorTimeouts.Default.TotalSeconds, recommendation.FailureId, connectionName);
            return false;
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
