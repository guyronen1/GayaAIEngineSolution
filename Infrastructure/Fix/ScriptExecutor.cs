using System.Diagnostics;
using MaiaAI.Core.Entities;
using MaiaAI.Core.Enums;
using MaiaAI.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace MaiaAI.Infrastructure.Fix;

/// <summary>
/// Executes a fix by running a shell or PowerShell script.
/// ActionPayload format: "executable [arguments]"
/// Supports {failureId} placeholder substitution in arguments.
/// Examples:
///   powershell.exe -File C:\scripts\retry.ps1 -FailureId {failureId}
///   cmd.exe /c C:\scripts\fix.bat {failureId}
/// The process must exit with code 0 for the fix to be considered successful.
/// </summary>
public sealed class ScriptExecutor(ILogger<ScriptExecutor> logger) : IFixActionExecutor
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    public FixActionType ActionType => FixActionType.Script;

    public async Task<bool> ExecuteAsync(
        string? payload,
        AiRecommendation recommendation,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            logger.LogError("ScriptExecutor: ActionPayload (command) is required for Failure {FailureId}",
                recommendation.FailureId);
            return false;
        }

        var command = payload.Replace("{failureId}", recommendation.FailureId.ToString(),
            StringComparison.OrdinalIgnoreCase);

        // Split "executable [rest of args]"
        var parts      = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var executable = parts[0];
        var arguments  = parts.Length > 1 ? parts[1] : string.Empty;

        var psi = new ProcessStartInfo
        {
            FileName               = executable,
            Arguments              = arguments,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };

        try
        {
            using var process = new Process { StartInfo = psi };
            process.Start();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(DefaultTimeout);

            await process.WaitForExitAsync(cts.Token);

            if (process.ExitCode == 0)
            {
                logger.LogInformation(
                    "ScriptExecutor: '{Executable}' exited 0 for Failure {FailureId}",
                    executable, recommendation.FailureId);
                return true;
            }

            var stderr = await process.StandardError.ReadToEndAsync(ct);
            logger.LogWarning(
                "ScriptExecutor: '{Executable}' exited {ExitCode} for Failure {FailureId}. Stderr: {Stderr}",
                executable, process.ExitCode, recommendation.FailureId, stderr);
            return false;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning(
                "ScriptExecutor: '{Executable}' timed out after {Seconds}s for Failure {FailureId}",
                executable, DefaultTimeout.TotalSeconds, recommendation.FailureId);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "ScriptExecutor: Failed to launch '{Executable}' for Failure {FailureId}",
                executable, recommendation.FailureId);
            return false;
        }
    }
}
