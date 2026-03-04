using System.Threading.Tasks;
using PCDiagnosticPro.AI.Models;

namespace PCDiagnosticPro.AI.Interfaces
{
    public interface ILlmModelLoader
    {
        ModelStatus Status { get; }
        string StatusMessage { get; }

        ModelValidationResult ValidateModelPath(string path, bool computeChecksum = false);

        Task<bool> TryLoadAsync(string modelPath, int contextWindow, int threads, int gpuLayers);

        void Unload();
    }
}
