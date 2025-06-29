
using System.Text;
using System.IO; 
using System.Linq; 
using System.Threading.Tasks; 
using System.Collections.Generic; 
using Microsoft.Extensions.Logging; 
using WebCodeWorkExecutor.Dtos; 
using WebCodeWorkExecutor.Services; 

namespace GenericRunnerApi.Services 
{
    public class RustSpecificLogicProvider : ILanguageSpecificLogic
    {
        private readonly ILogger<RustSpecificLogicProvider> _logger;
        private readonly IProcessRunner _processRunner;
        private const string RUST_COMPILER = "rustc";
        
        
        private const string DEFAULT_SOURCE_FILE_NAME = "main.rs";
        private const string DEFAULT_OUTPUT_EXEC_NAME = "solution_exec"; 

        public RustSpecificLogicProvider(ILogger<RustSpecificLogicProvider> logger, IProcessRunner processRunner)
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
            string localCodePathLocal = Path.Combine(workingDirectory, DEFAULT_SOURCE_FILE_NAME);
            try
            {
                
                string contentToWrite = codeContent;
                if (!string.IsNullOrEmpty(contentToWrite) && contentToWrite[0] == '\uFEFF')
                {
                    _logger.LogDebug("UTF-8 BOM detected in Rust code content. Removing it.");
                    contentToWrite = contentToWrite.Substring(1);
                }
                await File.WriteAllTextAsync(localCodePathLocal, contentToWrite, Encoding.UTF8);
                _logger.LogInformation("Rust code file written to {LocalCodePath}", localCodePathLocal);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write Rust solution code to local file {LocalCodePath}", localCodePathLocal);
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
                return (false, null, null, batchResponse);
            }
            return (true, DEFAULT_SOURCE_FILE_NAME, localCodePathLocal, null);
        }

        public async Task<(bool isCompiled, BatchExecuteResponse? notCompiledError, string? outputExeName, string? localExePath, string compilerOutput)> TryCompiling(
            List<TestCaseEvaluationData> testCasesData,
            string workingDirectory,
            string? localCodePath,
            int submissionId) 
        {
            if (string.IsNullOrEmpty(localCodePath) || !File.Exists(localCodePath))
            {
                _logger.LogError("Rust code file path is null, empty, or file does not exist for compilation: {LocalCodePath}", localCodePath);
                var errorResponse = new BatchExecuteResponse { SubmissionId = submissionId, CompilationSuccess = false, CompilerOutput = "Internal Error: Code file not found for compilation." };
                errorResponse.TestCaseResults = testCasesData.Select(tc => new TestCaseResult { TestCaseId = tc.TestCaseId, Status = EvaluationStatus.CompileError, Message = "Code file missing." }).ToList();
                return (false, errorResponse, null, null, errorResponse.CompilerOutput ?? string.Empty);
            }

            string localExecPath = Path.Combine(workingDirectory, DEFAULT_OUTPUT_EXEC_NAME);
            if (File.Exists(localExecPath)) File.Delete(localExecPath); 

            
            string compileArgs = $"\"{localCodePath}\" -o \"{localExecPath}\"";
            _logger.LogInformation("Compiling Rust code: {RustCompiler} {Arguments}", RUST_COMPILER, compileArgs);

            
            var (exitCode, stdOut, stdErr, _, _, _) = await _processRunner.RunProcessAsync(
                RUST_COMPILER,
                compileArgs,
                workingDirectory,
                null, 
                30,   
                256   
            );

            string compileOutput = $"Stdout:\n{stdOut}\nStderr:\n{stdErr}".Trim();

            if (exitCode == 0 && File.Exists(localExecPath)) 
            {
                _logger.LogInformation("Rust compilation successful for {LocalCodePath}. Executable at {LocalExecPath}", localCodePath, localExecPath);
                return (true, null, DEFAULT_OUTPUT_EXEC_NAME, workingDirectory, compileOutput);
            }
            else
            {
                _logger.LogWarning("Rust compilation failed for {LocalCodePath}. Exit Code: {ExitCode}. Output:\n{CompilerOutput}", localCodePath, exitCode, compileOutput);
                var batchResponse = new BatchExecuteResponse
                {
                    CompilationSuccess = false,
                    CompilerOutput = compileOutput,
                    SubmissionId = submissionId,
                    TestCaseResults = testCasesData.Select(tc => new TestCaseResult
                    {
                        TestCaseId = tc.TestCaseId,
                        Status = EvaluationStatus.CompileError,
                        Message = exitCode != 0 ? "Compilation error detected." : "Compilation failed: Executable not produced."
                    }).ToList()
                };
                return (false, batchResponse, null, null, compileOutput);
            }
        }

        public async Task<TestCaseResult> TryRunningTestcase(
            string workingDirectory,      
            string? executableName,        
            TestCaseEvaluationData tcData)
        {

            executableName = DEFAULT_OUTPUT_EXEC_NAME;
            string currentInputFileName = "current_input.txt";
            var currentLocalInputPath = Path.Combine(workingDirectory, currentInputFileName);
            var tcResult = new TestCaseResult { TestCaseId = tcData.TestCaseId };

            if (string.IsNullOrEmpty(executableName))
            {
                _logger.LogError("Executable name not provided for Rust execution for test case ID {TestCaseId}", tcData.TestCaseId);
                tcResult.Status = EvaluationStatus.InternalError;
                tcResult.Message = "Internal error: Executable name missing for execution.";
                return tcResult;
            }

            string localExecutablePath = Path.Combine(workingDirectory, executableName);

            if (!File.Exists(localExecutablePath))
            {
                _logger.LogError("Rust executable file not found for execution: {LocalExecutablePath}", localExecutablePath);
                tcResult.Status = EvaluationStatus.InternalError;
                tcResult.Message = "Executable file not found (post-compilation check).";
                return tcResult;
            }

            try
            {
                await File.WriteAllTextAsync(currentLocalInputPath, tcData.InputContent, Encoding.UTF8);

                string timeoutCommand = "timeout";
                int timeLimitForTimeoutCmd = (tcData.TimeLimitMs / 1000) > 0 ? (tcData.TimeLimitMs / 1000) : 1;
                
                string commandToRunWithTimeout = $"\"{localExecutablePath}\"";
                string timeoutArgs = $"--signal=SIGKILL {timeLimitForTimeoutCmd}s {commandToRunWithTimeout}";

                _logger.LogInformation("Executing Rust program for test case ID {TestCaseId}: {TimeoutCommand} {TimeoutArgs}", tcData.TestCaseId ?? "N/A", timeoutCommand, timeoutArgs);

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

                if (memoryLimitExceeded)
                {
                    tcResult.Status = EvaluationStatus.MemoryLimitExceeded;
                }
                else if (timedOut || runExitCode == 137 || runExitCode == 124) 
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
                _logger.LogError(ex, "Error running Rust test case (ID: {TestCaseId})", tcData.TestCaseId ?? "N/A");
                tcResult.Status = EvaluationStatus.InternalError;
                tcResult.Message = $"Error during Rust test case execution: {ex.Message}";
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