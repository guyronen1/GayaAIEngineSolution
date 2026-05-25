using MaiaAI.Core.Results;

namespace MaiaAI.Core.Interfaces.UseCases;

public interface IDirectoryPipelineUseCase
{
    Task<DirectoryPipelineResult> ExecuteAsync(
        string directoryPath,
        string searchPattern = "*.log",
        bool recursive = true,
        CancellationToken ct = default);
}
