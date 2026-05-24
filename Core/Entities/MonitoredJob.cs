using System.ComponentModel.DataAnnotations.Schema;
using MaiaAI.Core.Enums;

namespace MaiaAI.Core.Entities;

/// <summary>
/// Registry of monitored processes.
/// Each job has one ScanType (FK → ScanTypes) and a set of ScanCheckRules (1:many)
/// that define exactly what to detect during each scan.
/// </summary>
public class MonitoredJob
{
    public int MonitoredJobId { get; set; }

    /// <summary>Machine-friendly unique name, e.g. "ETL_Sales_Daily".</summary>
    public required string Name { get; set; }

    public string? DisplayName { get; set; }
    public int     JobTypeId   { get; set; }

    // ── Scan type (FK relation → ScanTypes table) ─────────────────────────────
    /// <summary>1 = FileSystem, 2 = Database, 3 = ApiEndpoint.</summary>
    public int ScanTypeId { get; set; } = 1;
    public ScanTypeDefinition? ScanTypeDefinition { get; set; }

    /// <summary>Resolved from ScanTypeDefinition.Name — requires eager loading of ScanTypeDefinition.</summary>
    [NotMapped]
    public ScanType ScanType
        => ScanTypeDefinition is null
               ? ScanType.FileSystem
               : Enum.Parse<ScanType>(ScanTypeDefinition.Name);

    // ── FileSystem scan config ────────────────────────────────────────────────
    /// <summary>Root folder where log files are stored, e.g. "c:\logs".</summary>
    public string? LogFolder { get; set; }

    /// <summary>Comma-separated glob patterns, e.g. "Trap*.log,Trap*.txt".</summary>
    public string? SearchPatterns { get; set; }

    // ── Database scan config ──────────────────────────────────────────────────
    /// <summary>Named connection string key in appsettings (resolves at runtime).</summary>
    public string? ConnectionName { get; set; }

    // ── ApiEndpoint scan config ───────────────────────────────────────────────
    /// <summary>URL to poll; non-2xx or matched body rules trigger a failure.</summary>
    public string? LogSourceUrl { get; set; }

    // ── Scheduling ────────────────────────────────────────────────────────────
    public int  PollingIntervalSeconds { get; set; } = 300;
    public bool IsActive               { get; set; } = true;
    public string?   Description { get; set; }
    public DateTime  CreatedAt   { get; set; }

    // ── Navigation ────────────────────────────────────────────────────────────
    public JobType? JobType { get; set; }

    /// <summary>What to detect during each scan — one rule per check.</summary>
    public ICollection<ScanCheckRule>    ScanCheckRules { get; set; } = [];

    /// <summary>Per-job classification rule overrides (for the classify step).</summary>
    public ICollection<MonitoredJobRule> JobRules       { get; set; } = [];

    public ICollection<JobFailure>       Failures       { get; set; } = [];

    /// <summary>Runtime coordination row (1:1). Created automatically on insert.</summary>
    public MonitoredJobLease? Lease { get; set; }
}
