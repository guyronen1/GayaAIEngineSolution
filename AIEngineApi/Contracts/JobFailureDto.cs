using MaiaAI.Core.Entities;

namespace AIEngineAPI.Contracts;

public sealed record JobFailureDto(
    int      FailureId,
    int      JobId,
    string?  StepName,
    string?  SourceId,
    string?  ErrorMessage,
    DateTime DetectedAt,
    string   Status,
    string   JobTypeName,
    string?  ErrorTypeCode,
    string?  MonitoredJobName)
{
    public static JobFailureDto From(JobFailure f) => new(
        f.FailureId,
        f.JobId,
        f.StepName,
        f.SourceId,
        f.ErrorMessage,
        f.DetectedAt,
        f.Status.ToString(),
        f.JobType?.Name   ?? f.JobTypeId.ToString(),
        f.ErrorType?.Code,
        f.MonitoredJob?.Name);
}
