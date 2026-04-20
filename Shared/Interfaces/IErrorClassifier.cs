using System.Threading.Tasks;

namespace Shared.Interfaces
{
    public interface IErrorClassifier
    {
        Task ScanAndClassifyFailuresAsync();
    }
}
