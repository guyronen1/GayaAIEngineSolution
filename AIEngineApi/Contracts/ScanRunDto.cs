using MaiaAI.Core.Entities;

namespace AIEngineAPI.Contracts;

public sealed record ScanRunDto(
    int      ScanRunId,
    int      MonitoredJobId,
    string?  MonitoredJobName,
    string   LeasedBy,
    DateTime StartedAt,
    DateTime CompletedAt,
    int      DurationMs,
    string   Outcome,
    string?  Error,
    int      FailuresDetected,
    int      Classifications,
    int      Recommendations)
{
    public static ScanRunDto From(ScanRunHistory r) => new(
        r.ScanRunId,
        r.MonitoredJobId,
        r.MonitoredJob?.Name,
        r.LeasedBy,
        r.StartedAt,
        r.CompletedAt,
        r.DurationMs,
        r.Outcome.ToString(),
        r.Error,
        r.FailuresDetected,
        r.Classifications,
        r.Recommendations);
}
