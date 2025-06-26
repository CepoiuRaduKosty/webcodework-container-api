
using WebCodeWorkExecutor.Dtos;
using System.Diagnostics;
using System.Text;
using WebCodeWorkExecutor.Services;

namespace GenericRunnerApi.Services
{
    public class MainEvaluationLogic : ICodeEvaluationLogic
    {
        private readonly ILogger<MainEvaluationLogic> _logger;
        private readonly ILanguageSpecificLogic _languageSpecificLogic;

        public MainEvaluationLogic(ILogger<MainEvaluationLogic> logger, ILanguageSpecificLogic languageSpecificLogic)
        {
            _logger = logger;
            _languageSpecificLogic = languageSpecificLogic;
        }


        public async Task<BatchExecuteResponse> EvaluateBatchAsync(
            string codeContent,
            List<TestCaseEvaluationData> testCasesData,
            string workingDirectory)
        {
            (bool isCodeFileCreated, string? codeFileName, string? localCodePath, BatchExecuteResponse? codeFileNotCreatedResponse) = await _languageSpecificLogic.TryCreateCodeFile(codeContent, testCasesData, workingDirectory);
            if (!isCodeFileCreated)
            {
                return codeFileNotCreatedResponse!;
            }

            var batchResponse = new BatchExecuteResponse();

            _logger.LogInformation("Compiling {CodeFile}...", codeFileName);
            (bool isCompiled, BatchExecuteResponse? notCompiledError, string? outputExeName, string? localExePath, string compilerOutput) = await _languageSpecificLogic.TryCompiling(testCasesData, workingDirectory, localCodePath);
            if (!isCompiled)
            {
                return notCompiledError!;
            }
            _logger.LogInformation("Compilation successful.");
            batchResponse.CompilerOutput = compilerOutput;
            batchResponse.CompilationSuccess = isCompiled;

            foreach (var tcData in testCasesData)
            {
                _logger.LogInformation("Running test case (ID: {TestCaseId})...", tcData.TestCaseId ?? "N/A");
                var tcresult = await _languageSpecificLogic.TryRunningTestcase(workingDirectory, localExePath, tcData);
                batchResponse.TestCaseResults.Add(tcresult);
            } 

            SafelyDeleteFile(localCodePath);
            SafelyDeleteFile(localExePath);

            return batchResponse;
        }

        private void SafelyDeleteFile(string? path)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try { File.Delete(path); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete temporary file {FilePath}", path); }
            }
        }
    }
}