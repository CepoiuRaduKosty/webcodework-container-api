
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
    public class JavaSpecificLogicProvider : ILanguageSpecificLogic
    {
        private readonly ILogger<JavaSpecificLogicProvider> _logger;
        private readonly IProcessRunner _processRunner;
        private const string JAVA_COMPILER = "javac";
        private const string JAVA_RUNTIME = "java";
        private const string DEFAULT_MAIN_CLASS_NAME = "Solution"; 
        private const string DEFAULT_SOURCE_FILE_NAME = DEFAULT_MAIN_CLASS_NAME + ".java";

        public JavaSpecificLogicProvider(ILogger<JavaSpecificLogicProvider> logger, IProcessRunner processRunner)
        {
            _logger = logger;
            _processRunner = processRunner;
        }
        
        private static string RemoveBom(string p)
        {
            string BOMMarkUtf8 = Encoding.UTF8.GetString(Encoding.UTF8.GetPreamble());
            if (p.StartsWith(BOMMarkUtf8, StringComparison.Ordinal))
                p = p.Remove(0, BOMMarkUtf8.Length);
            return p.Replace("\0", "");
        }

        public async Task<(bool isCodeFileCreated, string? codeFileName, string? localCodePath, BatchExecuteResponse? responseIfFailure)> TryCreateCodeFile(
            string codeContentUnclean,
            List<TestCaseEvaluationData> testCasesData,
            string workingDirectory)
        {
            string localCodePathLocal = Path.Combine(workingDirectory, DEFAULT_SOURCE_FILE_NAME);
            string codeContent = "";
            codeContent = codeContentUnclean.Trim(new char[] { '\uFEFF', '\u200B', '\ufeff' });
            codeContent = RemoveBom(codeContent);

            try
            {
                await File.WriteAllTextAsync(localCodePathLocal, codeContent, Encoding.ASCII);
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
            string? localCodePath) 
        {
            if (string.IsNullOrEmpty(localCodePath) || !File.Exists(localCodePath))
            {
                _logger.LogError("Java code file path is null, empty, or file does not exist for compilation: {LocalCodePath}", localCodePath);
                var errorResponse = new BatchExecuteResponse { CompilationSuccess = false, CompilerOutput = "Internal Error: Code file not found for compilation." };
                errorResponse.TestCaseResults = testCasesData.Select(tc => new TestCaseResult { TestCaseId = tc.TestCaseId, Status = EvaluationStatus.CompileError, Message = "Code file missing." }).ToList();
                return (false, errorResponse, null, null, errorResponse.CompilerOutput ?? string.Empty);
            }

            
            string compileArgs = $"-encoding UTF-8 -d . \"{localCodePath}\""; 
            _logger.LogInformation("Compiling Java code: {JavaCompiler} {Arguments}", JAVA_COMPILER, compileArgs);

            
            var (exitCode, stdOut, stdErr, debug1, debug2, debug3) = await _processRunner.RunProcessAsync(
                JAVA_COMPILER,
                compileArgs,
                workingDirectory,
                null, 
                30,   
                2048   
            );

            string compileOutput = $"Stdout:\n{stdOut}\nStderr:\n{stdErr}".Trim();

            if (exitCode == 0)
            {
                _logger.LogInformation("Java compilation successful for {LocalCodePath}. Output .class files should be in {WorkingDirectory}", localCodePath, workingDirectory);
                
                
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
            string workingDirectory,      
            string? mainClassName,         
            TestCaseEvaluationData tcData)
        {

            mainClassName = "Solution";
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

                string javaRuntimeCommand = JAVA_RUNTIME;
                string javaArgs = $"-cp \"{workingDirectory}\" {mainClassName}";

                string timeoutCommand = "timeout";
                int timeLimitForTimeoutCmd = (tcData.TimeLimitMs / 1000) > 0 ? (tcData.TimeLimitMs / 1000) : 1;
                string timeoutArgs = $"--signal=SIGKILL {timeLimitForTimeoutCmd}s {javaRuntimeCommand} {javaArgs}";

                _logger.LogInformation("Executing Java for test case ID {TestCaseId}: {TimeoutCommand} {TimeoutArgs}", tcData.TestCaseId ?? "N/A", timeoutCommand, timeoutArgs);

                var (runExitCode, runStdOut, runStdErr, durationMs, timedOut, memoryLimitExceededByProcessPolling) = await _processRunner.RunProcessAsync(
                    timeoutCommand,
                    timeoutArgs,
                    workingDirectory,
                    currentLocalInputPath,
                    timeLimitForTimeoutCmd + 5, 
                    tcData.MaxRamMB + 64                    
                );

                tcResult.Stdout = runStdOut.TrimEnd('\r', '\n');
                tcResult.Stderr = runStdErr.TrimEnd('\r', '\n');
                tcResult.DurationMs = durationMs;
                tcResult.ExitCode = runExitCode;
                
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