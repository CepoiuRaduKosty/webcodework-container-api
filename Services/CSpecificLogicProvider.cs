using System.Text;
using WebCodeWorkExecutor.Dtos;
using WebCodeWorkExecutor.Services;

namespace GenericRunnerApi.Services
{
    public class CSpecificLogicProvider : ILanguageSpecificLogic
    {
        private readonly ILogger<CSpecificLogicProvider> _logger;
        private readonly IProcessRunner _processRunner;

        public CSpecificLogicProvider(ILogger<CSpecificLogicProvider> logger, IProcessRunner processRunner)
        {
            _logger = logger;
            _processRunner = processRunner;
        }

        public async Task<(bool isCompiled, BatchExecuteResponse? notCompiledError, string? outputExeName, string? localExePath, string compilerOutput)> TryCompiling(List<TestCaseEvaluationData> testCasesData, string workingDirectory, string? localCodePath, int submissionId)
        {
            string outputExeName = "solution";
            string localExePath = Path.Combine(workingDirectory, outputExeName);
            if (File.Exists(localExePath)) File.Delete(localExePath);
            string compileArgs = $"\"{localCodePath}\" -o \"{localExePath}\" -O2 -Wall -lm";
            var (compileExitCode, compileStdOut, compileStdErr, _, _, _) = await _processRunner.RunProcessAsync("gcc", compileArgs, workingDirectory, null, 30, 4096);

            var batchResponse = new BatchExecuteResponse { SubmissionId = submissionId};
            batchResponse.CompilerOutput = $"Stdout:\n{compileStdOut}\nStderr:\n{compileStdErr}".Trim();
            batchResponse.CompilationSuccess = compileExitCode == 0;

            if (!batchResponse.CompilationSuccess)
            {
                _logger.LogWarning("Compilation failed. Exit Code: {ExitCode}. Output:\n{CompilerOutput}", compileExitCode, batchResponse.CompilerOutput);
                
                batchResponse.TestCaseResults = testCasesData.Select(tc => new TestCaseResult
                {
                    TestCaseId = tc.TestCaseId,
                    Status = EvaluationStatus.CompileError,
                    Message = "Compilation failed. Local code path " + localCodePath + " | local exe path " + localExePath,
                }).ToList();
                SafelyDeleteFile(localCodePath);
                return (isCompiled: false, notCompiledError: batchResponse, outputExeName: null, localExePath: null, compilerOutput: $"Stdout:\n{compileStdOut}\nStderr:\n{compileStdErr}".Trim());
            }
            return (isCompiled: true, notCompiledError: null, outputExeName, localExePath, compilerOutput: $"Stdout:\n{compileStdOut}\nStderr:\n{compileStdErr}".Trim());
        }

        public async Task<(bool isCodeFileCreated, string? codeFileName, string? localCodePath, BatchExecuteResponse? responseIfFailure)> TryCreateCodeFile(string codeContent, List<TestCaseEvaluationData> testCasesData, string workingDirectory, int submissionId)
        {
            string codeFileNameLocal = "solution.c";
            string localCodePathLocal = Path.Combine(workingDirectory, codeFileNameLocal);
            try
            {
                await File.WriteAllTextAsync(localCodePathLocal, codeContent, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write solution code to local file {LocalCodePath}", localCodePathLocal);
                var batchResponse = new BatchExecuteResponse { SubmissionId = submissionId};
                batchResponse.CompilationSuccess = false;
                batchResponse.CompilerOutput = "Internal error: Failed to write solution code for compilation.";
                batchResponse.TestCaseResults = testCasesData.Select(tc => new TestCaseResult { TestCaseId = tc.TestCaseId, Status = EvaluationStatus.InternalError, Message = "Setup failed." }).ToList();
                return (isCodeFileCreated: false, codeFileName: null, localCodePath: null, responseIfFailure: batchResponse);
            }

            return (isCodeFileCreated: true, codeFileName: codeFileNameLocal, localCodePath: localCodePathLocal, responseIfFailure: null);
        }

        public async Task<TestCaseResult> TryRunningTestcase(string workingDirectory, string? localExePath, TestCaseEvaluationData tcData)
        {
            var tcResult = new TestCaseResult { TestCaseId = tcData.TestCaseId };
            try
            {
                string runCommand = "timeout";
                int timeLimitForTimeoutCmd = (tcData.TimeLimitMs / 1000) > 0 ? (tcData.TimeLimitMs / 1000) : 1; 

                string runArgs = $"--signal=SIGKILL {timeLimitForTimeoutCmd}s \"{localExePath}\"";
                var (runExitCode, runStdOut, runStdErr, durationMs, timedOut, memoryLimitExceeded) = await _processRunner.RunProcessAsync(
                    runCommand, runArgs, workingDirectory, tcData.InputContent, timeLimitForTimeoutCmd + 2, tcData.MaxRamMB); 

                tcResult.Stdout = runStdOut.TrimEnd('\r', '\n');
                tcResult.Stderr = runStdErr.TrimEnd('\r', '\n');
                tcResult.DurationMs = durationMs;
                tcResult.ExitCode = runExitCode;
                tcResult.MaximumMemoryException = memoryLimitExceeded;

                if (memoryLimitExceeded)
                {
                    tcResult.Status = EvaluationStatus.MemoryLimitExceeded;
                }
                if (timedOut)
                {
                    tcResult.Status = EvaluationStatus.TimeLimitExceeded;
                }
                else if (runExitCode != 0)
                {
                    tcResult.Status = EvaluationStatus.RuntimeError;
                }
                else
                {
                    if (CompareOutputs(runStdOut, tcData.ExpectedOutputContent))
                    {
                        tcResult.Status = EvaluationStatus.Accepted;
                    }
                    else
                    {
                        tcResult.Status = EvaluationStatus.WrongAnswer;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error running test case (ID: {TestCaseId})", tcData.TestCaseId ?? "N/A");
                tcResult.Status = EvaluationStatus.InternalError;
                tcResult.Message = $"Error during test case execution: {ex.Message}";
            }

            return tcResult;
        }

        private void SafelyDeleteFile(string? path)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try { File.Delete(path); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete temporary file {FilePath}", path); }
            }
        }

        private bool CompareOutputs(string actualOutput, string expectedOutput)
        {
            var actualNormalized = NormalizeOutput(actualOutput);
            var expectedNormalized = NormalizeOutput(expectedOutput);
            return string.Equals(actualNormalized, expectedNormalized, StringComparison.Ordinal);
        }
        
        private string NormalizeOutput(string output)
        {
            if (string.IsNullOrEmpty(output)) return "";
            var lines = output.Replace("\r\n", "\n").Split('\n');
            var trimmedLines = lines.Select(line => line.TrimEnd());
            return string.Join("\n", trimmedLines).TrimEnd();
        }

    }
}