namespace MaiaAI.Core.Interfaces;

public interface ILogReader
{
    Task<string> ReadAsync(string path, CancellationToken ct = default);
}
