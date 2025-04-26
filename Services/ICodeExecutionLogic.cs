// Services/ICodeExecutionLogic.cs
using GenericRunnerApi.Dtos; // Assuming DTOs are in this namespace

namespace GenericRunnerApi.Services
{
    public interface ICodeExecutionLogic
    {
        Task<CompileResponse> CompileAsync(CompileRequest request, string workingDirectory);
        Task<RunResponse> RunAsync(RunRequest request, string workingDirectory);
    }
}