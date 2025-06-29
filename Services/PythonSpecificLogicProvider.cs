
using System.Text;
using WebCodeWorkExecutor.Dtos; 
using WebCodeWorkExecutor.Services; 

namespace GenericRunnerApi.Services 
{
    public class PythonSpecificLogicProvider : ILanguageSpecificLogic
    {
        private readonly ILogger<PythonSpecificLogicProvider> _logger;
        private readonly IProcessRunner _processRunner;
        private const string PYTHON_EXECUTABLE = "python3";

        public PythonSpecificLogicProvider(ILogger<PythonSpecificLogicProvider> logger, IProcessRunner processRunner)
        {
            _logger = logger;
            _processRunner = processRunner;
        }

        public async Task<(bool isCodeFileCreated, string? codeFileName, string? localCodePath, BatchExecuteResponse? responseIfFailure)> TryCreateCodeFile(
            string codeContent,
            List<TestCaseEvaluationData> testCasesData, 
            string workingDirectory,
            int submissionId)
        {
            string codeFileNameLocal = "solution.py"; 
            string localCodePathLocal = Path.Combine(workingDirectory, codeFileNameLocal);
            try
            {
                await File.WriteAllTextAsync(localCodePathLocal, codeContent, Encoding.UTF8);
                _logger.LogInformation("Python code file written to {LocalCodePath}", localCodePathLocal);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write Python solution code to local file {LocalCodePath}", localCodePathLocal);
                var batchResponse = new BatchExecuteResponse
                {
                    CompilationSuccess = false,
                    CompilerOutput = "Internal error: Failed to write solution code for evaluation.",
                    SubmissionId = submissionId,
                    TestCaseResults = testCasesData.Select(tc => new TestCaseResult
                    {
                        TestCaseId = tc.TestCaseId,
                        Status = EvaluationStatus.InternalError, 
                        Message = "Setup failed: Could not write code file."
                    }).ToList()
                };
                return (isCodeFileCreated: false, codeFileName: null, localCodePath: null, responseIfFailure: batchResponse);
            }

            return (isCodeFileCreated: true, codeFileName: codeFileNameLocal, localCodePath: localCodePathLocal, responseIfFailure: null);
        }


        public async Task<(bool isCompiled, BatchExecuteResponse? notCompiledError, string? outputExeName, string? localExePath, string compilerOutput)> TryCompiling(
            List<TestCaseEvaluationData> testCasesData, 
            string workingDirectory,
            string? localCodePath,
            int submissionId)
        {
            if (string.IsNullOrEmpty(localCodePath) || !File.Exists(localCodePath))
            {
                _logger.LogError("Python code file path is null, empty, or file does not exist for compilation: {LocalCodePath}", localCodePath);
                var errorResponse = new BatchExecuteResponse { SubmissionId = submissionId, CompilationSuccess = false, CompilerOutput = "Internal Error: Code file not found for syntax check." };
                errorResponse.TestCaseResults = testCasesData.Select(tc => new TestCaseResult { TestCaseId = tc.TestCaseId, Status = EvaluationStatus.CompileError, Message = "Code file missing." }).ToList();
                return (isCompiled: false, notCompiledError: errorResponse, outputExeName: null, localExePath: null, compilerOutput: errorResponse.CompilerOutput);
            }

            string compileCheckArgs = $"-m py_compile \"{localCodePath}\"";
            _logger.LogInformation("Performing Python syntax check: {PythonExecutable} {Arguments}", PYTHON_EXECUTABLE, compileCheckArgs);

            var (exitCode, stdOut, stdErr, _, _, _) = await _processRunner.RunProcessAsync(
                PYTHON_EXECUTABLE,
                compileCheckArgs,
                workingDirectory,
                null, 
                10,   
                128    
            );

            string syntaxCheckOutput = $"Stdout:\n{stdOut}\nStderr:\n{stdErr}".Trim();

            if (exitCode == 0)
            {
                _logger.LogInformation("Python syntax check successful for {LocalCodePath}", localCodePath);
                return (isCompiled: true, notCompiledError: null, outputExeName: Path.GetFileName(localCodePath), localExePath: localCodePath, compilerOutput: syntaxCheckOutput);
            }
            else
            {
                _logger.LogWarning("Python syntax check failed for {LocalCodePath}. Exit Code: {ExitCode}. Output:\n{SyntaxCheckOutput}", localCodePath, exitCode, syntaxCheckOutput);
                var batchResponse = new BatchExecuteResponse
                {
                    CompilationSuccess = false,
                    CompilerOutput = syntaxCheckOutput,
                    SubmissionId = submissionId,
                    TestCaseResults = testCasesData.Select(tc => new TestCaseResult
                    {
                        TestCaseId = tc.TestCaseId,
                        Status = EvaluationStatus.CompileError, 
                        Message = "Syntax error detected."
                    }).ToList()
                };
                return (isCompiled: false, notCompiledError: batchResponse, outputExeName: null, localExePath: null, compilerOutput: syntaxCheckOutput);
            }
        }

        public async Task<TestCaseResult> TryRunningTestcase(
            string workingDirectory,
            string? localScriptPath, 
            TestCaseEvaluationData tcData)
        {
            string currentInputFileName = "current_input.txt";
            var currentLocalInputPath = Path.Combine(workingDirectory, currentInputFileName);

            var tcResult = new TestCaseResult { TestCaseId = tcData.TestCaseId };

            if (string.IsNullOrEmpty(localScriptPath) || !File.Exists(localScriptPath))
            {
                _logger.LogError("Python script file path is null, empty, or file does not exist for execution: {LocalScriptPath}", localScriptPath);
                tcResult.Status = EvaluationStatus.InternalError;
                tcResult.Message = "Script file not found for execution (post-compilation check).";
                return tcResult;
            }

            try
            {
                await File.WriteAllTextAsync(currentLocalInputPath, tcData.InputContent, Encoding.UTF8);
                string commandToRun = PYTHON_EXECUTABLE;
                string scriptArg = $"\"{localScriptPath}\"";

                string timeoutCommand = "timeout";
                int timeLimitForTimeoutCmd = (tcData.TimeLimitMs / 1000) > 0 ? (tcData.TimeLimitMs / 1000) : 1;
                string timeoutArgs = $"--signal=SIGKILL {timeLimitForTimeoutCmd}s {commandToRun} {scriptArg}";

                _logger.LogInformation("Executing Python script for test case ID {TestCaseId}: {TimeoutCommand} {TimeoutArgs}", tcData.TestCaseId ?? "N/A", timeoutCommand, timeoutArgs);

                var (runExitCode, runStdOut, runStdErr, durationMs, timedOut, memoryLimitExceeded) = await _processRunner.RunProcessAsync(
                    timeoutCommand,
                    timeoutArgs,    
                    workingDirectory,
                    currentLocalInputPath,
                    timeLimitForTimeoutCmd + 2,
                    tcData.MaxRamMB
                );

                tcResult.Stdout = runStdOut.TrimEnd('\r', '\n');
                tcResult.Stderr = runStdErr.TrimEnd('\r', '\n');
                tcResult.DurationMs = durationMs;
                tcResult.ExitCode = runExitCode;
                tcResult.MaximumMemoryException = memoryLimitExceeded; 

                if (memoryLimitExceeded)
                {
                    tcResult.Status = EvaluationStatus.MemoryLimitExceeded;
                }
                else if (timedOut)
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
                _logger.LogError(ex, "Error running Python test case (ID: {TestCaseId})", tcData.TestCaseId ?? "N/A");
                tcResult.Status = EvaluationStatus.InternalError;
                tcResult.Message = $"Error during Python test case execution: {ex.Message}";
            }
            finally
            {
                SafelyDeleteFile(currentLocalInputPath);
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