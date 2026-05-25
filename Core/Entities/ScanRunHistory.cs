using MaiaAI.Core.Enums;

namespace MaiaAI.Core.Entities;

/// <summary>
/// One row per completed worker-tick scan of a MonitoredJob. Append-only — never
/// updated after insert. Bounded by the ScanHistoryRetentionWorker (default 30 days).
/// </summary>
public class ScanRunHistory
{
    public int      ScanRunId        { get; set; }
    public int      MonitoredJobId   { get; set; }
    /// <summary>Worker identity that owned the lease for this run ("host=...;pid=...;runId=...").</summary>
    public required string LeasedBy  { get; set; }
    public DateTime StartedAt        { get; set; }
    public DateTime CompletedAt      { get; set; }
    /// <summary>Convenience field — CompletedAt minus StartedAt in ms.</summary>
    public int      DurationMs       { get; set; }
    public JobRunOutcome Outcome     { get; set; }
    public string?  Error            { get; set; }
    public int      FailuresDetected { get; set; }
    public int      Classifications  { get; set; }
    public int      Recommendations  { get; set; }

    public MonitoredJob? MonitoredJob { get; set; }
}
