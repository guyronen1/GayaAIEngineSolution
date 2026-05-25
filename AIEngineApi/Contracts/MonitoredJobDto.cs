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
    // Database
    string?                          ConnectionName,
    // ApiEndpoint
    string?                          LogSourceUrl,
    int                              PollingIntervalSeconds,
    bool                             IsActive,
    string?                          Description,
    DateTime                         CreatedAt,
    IReadOnlyList<ScanCheckRuleDto>  ScanCheckRules,
    IReadOnlyList<RuleOverrideDto>   Rules)
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
            .ToList());
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
