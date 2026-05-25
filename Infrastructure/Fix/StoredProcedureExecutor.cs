using MaiaAI.Core.Entities;
using MaiaAI.Core.Enums;
using MaiaAI.Core.Interfaces;
using MaiaAI.Infrastructure.DataAccess;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MaiaAI.Infrastructure.Fix;

/// <summary>
/// Executes a fix by calling a SQL stored procedure.
/// ActionPayload format:
///   "SpName"                  — uses the default DB connection
///   "ConnectionName|SpName"   — reserved for future per-job connection support
/// The procedure receives @FailureId (int) as a parameter.
/// </summary>
public sealed class StoredProcedureExecutor(
    IDbContextFactory<AiDbContext> factory,
    ILogger<StoredProcedureExecutor> logger) : IFixActionExecutor
{
    public FixActionType ActionType => FixActionType.StoredProcedure;

    public async Task<bool> ExecuteAsync(
        string? payload,
        AiRecommendation recommendation,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            logger.LogError("StoredProcedureExecutor: ActionPayload (SP name) is required for Failure {FailureId}",
                recommendation.FailureId);
            return false;
        }

        // Parse "ConnectionName|SpName" or just "SpName"
        var spName = payload.Contains('|')
            ? payload.Split('|', 2)[1].Trim()
            : payload.Trim();

        if (!IsValidIdentifier(spName))
        {
            logger.LogError(
                "StoredProcedureExecutor: Invalid SP name '{SpName}' for Failure {FailureId}",
                spName, recommendation.FailureId);
            return false;
        }

        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var failureIdParam = new SqlParameter("@FailureId", recommendation.FailureId);

            await db.Database.ExecuteSqlRawAsync(
                $"EXEC {spName} @FailureId", failureIdParam, ct);

            logger.LogInformation(
                "StoredProcedureExecutor: Executed {SpName} for Failure {FailureId}",
                spName, recommendation.FailureId);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "StoredProcedureExecutor: {SpName} failed for Failure {FailureId}",
                spName, recommendation.FailureId);
            return false;
        }
    }

    // Allows: letters, digits, underscores, dots, brackets (schema.SpName, [dbo].[Sp])
    private static bool IsValidIdentifier(string name)
        => !string.IsNullOrWhiteSpace(name)
           && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '.' or '[' or ']');
}
