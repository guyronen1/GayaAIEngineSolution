namespace MaiaAI.Core.Interfaces.UseCases;

public interface IExecuteFixesUseCase
{
    Task ExecuteAsync(CancellationToken ct = default);
}
