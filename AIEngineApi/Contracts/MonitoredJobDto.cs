using MaiaAI.Core.Entities;
using MaiaAI.Core.Enums;

namespace AIEngineAPI.Contracts;

public sealed record MonitoredJobDto(
    int                              MonitoredJobId,
    string                           Name,
    string?                          DisplayName,
    string                           JobTypeName,
    int                              ScanTypeId,
    string                           ScanTypeName,
    // FileSystem
    string?                          LogFolder,
    string?                          SearchPatterns,
    string?                          InputFolder,
    // Database
    string?                          ConnectionName,
    // ApiEndpoint
    string?                          LogSourceUrl,
    int                              PollingIntervalSeconds,
    bool                             IsActive,
    string?                          Description,
    DateTime                         CreatedAt,
    IReadOnlyList<ScanCheckRuleDto>  ScanCheckRules,
    IReadOnlyList<RuleOverrideDto>   Rules,
    MonitoredJobLeaseDto?            Lease)
{
    public static MonitoredJobDto From(MonitoredJob m) => new(
        m.MonitoredJobId,
        m.Name,
        m.DisplayName,
        m.JobType?.Name        ?? m.JobTypeId.ToString(),
        m.ScanTypeId,
        m.ScanType.ToString(),
        m.LogFolder,
        m.SearchPatterns,
        m.InputFolder,
        m.ConnectionName,
        m.LogSourceUrl,
        m.PollingIntervalSeconds,
        m.IsActive,
        m.Description,
        m.CreatedAt,
        m.ScanCheckRules
            .Where(r => r.IsActive)
            .Select(ScanCheckRuleDto.From)
            .ToList(),
        m.JobRules
            .Where(jr => jr.IsActive && jr.Rule is not null)
            .Select(jr => RuleOverrideDto.From(jr.Rule!))
            .ToList(),
        // Null-safe: schema is 1:1 with cascade delete so Lease should always be
        // present, but treat absence as gray-state in the UI rather than NPE.
        MonitoredJobLeaseDto.From(m.Lease));
}

public sealed record MonitoredJobLeaseDto(
    string?   LeasedBy,
    DateTime? LeasedAt,
    DateTime? LeasedUntil,
    DateTime? NextEligibleAt,
    DateTime? LastRunStartedAt,
    DateTime? LastRunCompletedAt,
    string?   LastRunOutcome,
    string?   LastRunError,
    int?      LastRunDurationMs)
{
    public static MonitoredJobLeaseDto? From(MonitoredJobLease? l)
    {
        if (l is null) return null;
        int? durationMs = (l.LastRunStartedAt.HasValue && l.LastRunCompletedAt.HasValue)
            ? (int)Math.Clamp((l.LastRunCompletedAt.Value - l.LastRunStartedAt.Value).TotalMilliseconds,
                              0, int.MaxValue)
            : null;
        return new MonitoredJobLeaseDto(
            l.LeasedBy,
            l.LeasedAt,
            l.LeasedUntil,
            l.NextEligibleAt,
            l.LastRunStartedAt,
            l.LastRunCompletedAt,
            l.LastRunOutcome?.ToString(),
            l.LastRunError,
            durationMs);
    }
}

public sealed record ScanCheckRuleDto(
    int       CheckRuleId,
    string    CheckType,
    string?   SourceTable,
    string    TargetField,
    decimal?  MinValue,
    decimal?  MaxValue,
    string?   ExpectedValue,
    string?   WatermarkColumn,
    string?   SourceIdColumn,
    string?   FilePathColumn,
    string?   InputPathPattern,
    string    Severity,
    string?   Description)
{
    public static ScanCheckRuleDto From(ScanCheckRule r) => new(
        r.CheckRuleId,
        r.CheckType.ToString(),
        r.SourceTable,
        r.TargetField,
        r.MinValue,
        r.MaxValue,
        r.ExpectedValue,
        r.WatermarkColumn,
        r.SourceIdColumn,
        r.FilePathColumn,
        r.InputPathPattern,
        r.Severity.ToString(),
        r.Description);
}

public sealed record RuleOverrideDto(
    int     RuleId,
    string  Pattern,
    string  ErrorTypeCode,
    decimal Confidence,
    int     Priority)
{
    public static RuleOverrideDto From(ClassificationRule r) => new(
        r.RuleId,
        r.Pattern,
        r.ErrorType?.Code ?? r.ErrorTypeId.ToString(),
        r.Confidence,
        r.Priority);
}
