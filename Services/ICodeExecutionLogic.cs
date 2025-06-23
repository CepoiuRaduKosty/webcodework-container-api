
using WebCodeWorkExecutor.Dtos; 

namespace WebCodeWorkExecutor.Services
{
    public record TestCaseEvaluationData(
        string InputContent,
        string ExpectedOutputContent,
        int TimeLimitMs,
        int MaxRamMB,
        string? TestCaseId 
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