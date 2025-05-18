// Services/ICodeExecutionLogic.cs
using WebCodeWorkExecutor.Dtos; // Assuming DTOs are in this namespace

namespace WebCodeWorkExecutor.Services
{
    public record TestCaseEvaluationData(
        string InputContent,
        string ExpectedOutputContent,
        int TimeLimitMs,
        int MaxRamMB,
        string? TestCaseId // For correlation
    );

    public interface ICodeEvaluationLogic
    {
        Task<BatchExecuteResponse> EvaluateBatchAsync(
            string codeContent,
            List<TestCaseEvaluationData> testCasesData,
            string workingDirectory
        );
    }
}