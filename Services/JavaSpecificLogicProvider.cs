// Services/JavaSpecificLogicProvider.cs
using System.Text;
using System.IO; // Required for Path, File
using System.Linq; // Required for Linq methods
using System.Threading.Tasks; // Required for Task
using System.Collections.Generic; // Required for List
using Microsoft.Extensions.Logging; // Required for ILogger
using WebCodeWorkExecutor.Dtos; // Your DTOs namespace (ensure EvaluationStatus is here)
using WebCodeWorkExecutor.Services; // Your Services namespace (for IProcessRunner, ILanguageSpecificLogic)

namespace GenericRunnerApi.Services // Or WebCodeWorkExecutor.Services if preferred
{
    public class JavaSpecificLogicProvider : ILanguageSpecificLogic
    {
        private readonly ILogger<JavaSpecificLogicProvider> _logger;
        private readonly IProcessRunner _processRunner;
        private const string JAVA_COMPILER = "javac";
        private const string JAVA_RUNTIME = "java";
        private const string DEFAULT_MAIN_CLASS_NAME = "Solution"; // Assuming main class is Solution
        private const string DEFAULT_SOURCE_FILE_NAME = DEFAULT_MAIN_CLASS_NAME + ".java";

        public JavaSpecificLogicProvider(ILogger<JavaSpecificLogicProvider> logger, IProcessRunner processRunner)
        {
            _logger = logger;
            _processRunner = processRunner;
        }

        public async Task<(bool isCodeFileCreated, string? codeFileName, string? localCodePath, BatchExecuteResponse? responseIfFailure)> TryCreateCodeFile(
            string codeContent,
            List<TestCaseEvaluationData> testCasesData,
            string workingDirectory)
        {
            string localCodePathLocal = Path.Combine(workingDirectory, DEFAULT_SOURCE_FILE_NAME);
            try
            {
                await File.WriteAllTextAsync(localCodePathLocal, codeContent, Encoding.UTF8);
                _logger.LogInformation("Java code file written to {LocalCodePath}", localCodePathLocal);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write Java solution code to local file {LocalCodePath}", localCodePathLocal);
                var batchResponse = new BatchExecuteResponse
                {
                    CompilationSuccess = false,
                    CompilerOutput = "Internal error: Failed to write solution code for evaluation.",
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
            string? localCodePath) // Path to the Solution.java
        {
            if (string.IsNullOrEmpty(localCodePath) || !File.Exists(localCodePath))
            {
                _logger.LogError("Java code file path is null, empty, or file does not exist for compilation: {LocalCodePath}", localCodePath);
                var errorResponse = new BatchExecuteResponse { CompilationSuccess = false, CompilerOutput = "Internal Error: Code file not found for compilation." };
                errorResponse.TestCaseResults = testCasesData.Select(tc => new TestCaseResult { TestCaseId = tc.TestCaseId, Status = EvaluationStatus.CompileError, Message = "Code file missing." }).ToList();
                return (false, errorResponse, null, null, errorResponse.CompilerOutput ?? string.Empty);
            }

            // For Java, `javac Solution.java`. It produces Solution.class in the same directory.
            string compileArgs = $"\"{localCodePath}\""; // javac will create .class files in the workingDirectory
            _logger.LogInformation("Compiling Java code: {JavaCompiler} {Arguments}", JAVA_COMPILER, compileArgs);

            // Short timeout for compilation, memory for javac itself
            var (exitCode, stdOut, stdErr, _, _, _) = await _processRunner.RunProcessAsync(
                JAVA_COMPILER,
                compileArgs,
                workingDirectory,
                null, // No stdin for javac
                30,   // Compilation timeout (seconds) - Java can be slower
                256   // Memory for javac (MB)
            );

            string compileOutput = $"Stdout:\n{stdOut}\nStderr:\n{stdErr}".Trim();

            if (exitCode == 0)
            {
                _logger.LogInformation("Java compilation successful for {LocalCodePath}. Output .class files should be in {WorkingDirectory}", localCodePath, workingDirectory);
                // The "outputIdentifier" is the main class name.
                // The "executionBasePath" is the directory containing the .class files (the classpath).
                return (true, null, DEFAULT_MAIN_CLASS_NAME, workingDirectory, compileOutput);
            }
            else
            {
                _logger.LogWarning("Java compilation failed for {LocalCodePath}. Exit Code: {ExitCode}. Output:\n{CompilerOutput}", localCodePath, exitCode, compileOutput);
                var batchResponse = new BatchExecuteResponse
                {
                    CompilationSuccess = false,
                    CompilerOutput = compileOutput,
                    TestCaseResults = testCasesData.Select(tc => new TestCaseResult
                    {
                        TestCaseId = tc.TestCaseId,
                        Status = EvaluationStatus.CompileError,
                        Message = "Compilation error detected."
                    }).ToList()
                };
                return (false, batchResponse, null, null, compileOutput);
            }
        }

        public async Task<TestCaseResult> TryRunningTestcase(
            string workingDirectory,      // This is the base path where .class files are (e.g., /sandbox)
            string? mainClassName,         // This is the "outputIdentifier" from TryCompiling (e.g., "Solution")
            TestCaseEvaluationData tcData)
        {
            string currentInputFileName = "current_input.txt";
            var currentLocalInputPath = Path.Combine(workingDirectory, currentInputFileName);
            var tcResult = new TestCaseResult { TestCaseId = tcData.TestCaseId };

            if (string.IsNullOrEmpty(mainClassName))
            {
                _logger.LogError("Main class name not provided for Java execution for test case ID {TestCaseId}", tcData.TestCaseId);
                tcResult.Status = EvaluationStatus.InternalError;
                tcResult.Message = "Internal error: Main class name missing for execution.";
                return tcResult;
            }

            try
            {
                await File.WriteAllTextAsync(currentLocalInputPath, tcData.InputContent, Encoding.UTF8);

                // Command for Java execution: java -cp <classpath> <MainClassName>
                // Here, classpath is the workingDirectory.
                string javaRuntimeCommand = JAVA_RUNTIME;
                // For JVM memory limit, you'd use -Xmx (e.g. -Xmx128m).
                // This needs to be passed to `java`, not easily enforced by the generic `timeout` or C# Process memory polling.
                // For simplicity, we'll rely on Docker container memory limit first, but -Xmx is the proper way for JVM.
                // The `timeout` command will still wrap this for time limiting.
                string javaArgs = $"-Xmx{tcData.MaxRamMB}m -cp \"{workingDirectory}\" {mainClassName}";

                string timeoutCommand = "timeout";
                int timeLimitForTimeoutCmd = (tcData.TimeLimitMs / 1000) > 0 ? (tcData.TimeLimitMs / 1000) : 1;
                string timeoutArgs = $"--signal=SIGKILL {timeLimitForTimeoutCmd}s {javaRuntimeCommand} {javaArgs}";

                _logger.LogInformation("Executing Java for test case ID {TestCaseId}: {TimeoutCommand} {TimeoutArgs}", tcData.TestCaseId ?? "N/A", timeoutCommand, timeoutArgs);

                var (runExitCode, runStdOut, runStdErr, durationMs, timedOut, memoryLimitExceededByProcessPolling) = await _processRunner.RunProcessAsync(
                    timeoutCommand,
                    timeoutArgs,
                    workingDirectory,
                    currentLocalInputPath,
                    timeLimitForTimeoutCmd + 5, // Overall timeout for the 'timeout' process wrapper (a bit more generous)
                    tcData.MaxRamMB + 64 // Give JVM a bit more container memory than its -Xmx for overhead
                                         // Note: The process poller memory limit (`tcData.MaxRamMB`) might not be as effective
                                         // for JVM as the `-Xmx` flag is. The container limit is the ultimate cap.
                );

                tcResult.Stdout = runStdOut.TrimEnd('\r', '\n');
                tcResult.Stderr = runStdErr.TrimEnd('\r', '\n');
                tcResult.DurationMs = durationMs;
                tcResult.ExitCode = runExitCode;
                // tcResult.MaximumMemoryException = memoryLimitExceededByProcessPolling; // If this prop exists

                // JVM might exit with non-zero for OOM before our poller catches it or timeout command reacts.
                // SIGKILL from timeout results in exit code 137.
                if (memoryLimitExceededByProcessPolling || (runStdErr.Contains("java.lang.OutOfMemoryError")))
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
                _logger.LogError(ex, "Error running Java test case (ID: {TestCaseId})", tcData.TestCaseId ?? "N/A");
                tcResult.Status = EvaluationStatus.InternalError;
                tcResult.Message = $"Error during Java test case execution: {ex.Message}";
            }
            finally
            {
                SafelyDeleteFile(currentLocalInputPath);
            }
            return tcResult;
        }

        // --- Helper Methods (identical to CSpecificLogicProvider) ---
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