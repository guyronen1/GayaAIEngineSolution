namespace AIEngineAPI.Contracts;

public sealed record PipelineRequest(
    string  DirectoryPath,
    string? SearchPattern,
    bool?   Recursive);
