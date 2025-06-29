using WebCodeWorkExecutor.Dtos;

namespace WebCodeWorkExecutor.Services
{
    public interface ILanguageSpecificLogic
    {
        Task<TestCaseResult> TryRunningTestcase(string workingDirectory, string? localExePath, TestCaseEvaluationData tcData);

        Task<(bool isCompiled, BatchExecuteResponse? notCompiledError, string? outputExeName, string? localExePath, string compilerOutput)> TryCompiling(List<TestCaseEvaluationData> testCasesData, string workingDirectory, string? localCodePath, int submissionId);

        Task<(bool isCodeFileCreated, string? codeFileName, string? localCodePath, BatchExecuteResponse? responseIfFailure)> TryCreateCodeFile(string codeContent, List<TestCaseEvaluationData> testCasesData, string workingDirectory, int submissionId);
    }
}