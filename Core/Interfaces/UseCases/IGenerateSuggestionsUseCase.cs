using MaiaAI.Core.Results;

namespace MaiaAI.Core.Interfaces.UseCases;

public interface IGenerateSuggestionsUseCase
{
    Task ExecuteAsync(IEnumerable<ClassificationResult> results, CancellationToken ct = default);
}
