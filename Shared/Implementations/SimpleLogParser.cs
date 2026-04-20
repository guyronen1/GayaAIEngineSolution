using Shared.Interfaces;

namespace Shared.Implementations
{
    public class SimpleLogParser : ILogParser
    {
        public string[] ParseLog(string logContent)
        {
            return logContent.Split('\n');
        }
    }
}
