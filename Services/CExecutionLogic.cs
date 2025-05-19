// Services/CEvaluationLogic.cs
using WebCodeWorkExecutor.Dtos;
using System.Diagnostics;
using System.Text;
using WebCodeWorkExecutor.Services;
// REMOVE: using Azure.Storage.Blobs;
// REMOVE: using Azure.Storage.Blobs.Models;

namespace GenericRunnerApi.Services
{
    public class CEvaluationLogic : ICodeEvaluationLogic
    {
        private readonly ILogger<CEvaluationLogic> _logger;
        // REMOVE: BlobServiceClient and IConfiguration injections if no longer needed here

        public CEvaluationLogic(ILogger<CEvaluationLogic> logger) // Simplified constructor
        {
            _logger = logger;
        }

        public async Task<BatchExecuteResponse> EvaluateBatchAsync(
            string codeContent,
            List<TestCaseEvaluationData> testCasesData,
            string workingDirectory)
        {
            string codeFileName = "solution.c";
            string outputExeName = "solution";
            // Temporary local filenames for each test case run
            string currentInputFileName = "current_input.txt";
            string currentExpectedOutputFileName = "current_expected_output.txt";
            string currentStdoutFileName = "current_program.stdout";
            string currentStderrFileName = "current_program.stderr";

            string localCodePath = Path.Combine(workingDirectory, codeFileName);
            string localExePath = Path.Combine(workingDirectory, outputExeName);
            string localCompilerLogPath = Path.Combine(workingDirectory, "compiler.log");

            var batchResponse = new BatchExecuteResponse();

            // --- 1. Write Main Code File ---
            try
            {
                await File.WriteAllTextAsync(localCodePath, codeContent, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write solution code to local file {LocalCodePath}", localCodePath);
                batchResponse.CompilationSuccess = false;
                batchResponse.CompilerOutput = "Internal error: Failed to write solution code for compilation.";
                batchResponse.TestCaseResults = testCasesData.Select(tc => new TestCaseResult { TestCaseId = tc.TestCaseId, Status = EvaluationStatus.InternalError, Message = "Setup failed." }).ToList();
                return batchResponse;
            }

            // --- 2. Compile Once ---
            _logger.LogInformation("Compiling {CodeFile}...", codeFileName);
            if (File.Exists(localExePath)) File.Delete(localExePath);
            if (File.Exists(localCompilerLogPath)) File.Delete(localCompilerLogPath);

            string compileArgs = $"\"{localCodePath}\" -o \"{localExePath}\" -O2 -Wall -lm";
            var (compileExitCode, compileStdOut, compileStdErr, _, _) = await RunProcessAsync("gcc", compileArgs, workingDirectory, null, 30); // Increased compile timeout

            batchResponse.CompilerOutput = $"Stdout:\n{compileStdOut}\nStderr:\n{compileStdErr}".Trim();
            batchResponse.CompilationSuccess = compileExitCode == 0;

            if (!batchResponse.CompilationSuccess)
            {
                _logger.LogWarning("Compilation failed. Exit Code: {ExitCode}. Output:\n{CompilerOutput}", compileExitCode, batchResponse.CompilerOutput);
                // Populate all test case results with CompileError
                batchResponse.TestCaseResults = testCasesData.Select(tc => new TestCaseResult
                {
                    TestCaseId = tc.TestCaseId,
                    Status = EvaluationStatus.CompileError,
                    Message = "Compilation failed."
                }).ToList();
                SafelyDeleteFile(localCodePath); // Clean up solution.c
                return batchResponse;
            }
            _logger.LogInformation("Compilation successful.");

            // --- 3. Run Each Test Case ---
            foreach (var tcData in testCasesData)
            {
                _logger.LogInformation("Running test case (ID: {TestCaseId})...", tcData.TestCaseId ?? "N/A");
                var currentLocalInputPath = Path.Combine(workingDirectory, currentInputFileName);
                var currentLocalExpectedOutputPath = Path.Combine(workingDirectory, currentExpectedOutputFileName);
                var currentLocalStdoutPath = Path.Combine(workingDirectory, currentStdoutFileName);
                var currentLocalStderrPath = Path.Combine(workingDirectory, currentStderrFileName);

                var tcResult = new TestCaseResult { TestCaseId = tcData.TestCaseId };

                try
                {
                    // Write current test case input and expected output to local files
                    await File.WriteAllTextAsync(currentLocalInputPath, tcData.InputContent, Encoding.UTF8);
                    await File.WriteAllTextAsync(currentLocalExpectedOutputPath, tcData.ExpectedOutputContent, Encoding.UTF8);

                    if (File.Exists(currentLocalStdoutPath)) File.Delete(currentLocalStdoutPath);
                    if (File.Exists(currentLocalStderrPath)) File.Delete(currentLocalStderrPath);

                    string runCommand = "timeout"; // timeout command from coreutils
                    // Use tcData.TimeLimitMs (convert to seconds for timeout command)
                    // Docker container memory limit is set by orchestrator.
                    // Enforcing stricter per-test-case memory limits within the container
                    // would require more complex OS-level tools (like cgroups directly, or 'prlimit').
                    // For now, rely on Docker's overall container limit and specific time limit.
                    int timeLimitForTimeoutCmd = (tcData.TimeLimitMs / 1000) > 0 ? (tcData.TimeLimitMs / 1000) : 1; // Ensure at least 1s for timeout command

                    string runArgs = $"--signal=SIGKILL {timeLimitForTimeoutCmd}s \"{localExePath}\"";
                    var (runExitCode, runStdOut, runStdErr, durationMs, timedOut) = await RunProcessAsync(
                        runCommand, runArgs, workingDirectory, currentLocalInputPath, timeLimitForTimeoutCmd + 2); // Orchestrator timeout

                    tcResult.Stdout = runStdOut.TrimEnd('\r', '\n');
                    tcResult.Stderr = runStdErr.TrimEnd('\r', '\n');
                    tcResult.DurationMs = durationMs;
                    tcResult.ExitCode = runExitCode;

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
                        // Compare output
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
                finally
                {
                    SafelyDeleteFile(currentLocalInputPath);
                    SafelyDeleteFile(currentLocalExpectedOutputPath);
                    SafelyDeleteFile(currentLocalStdoutPath);
                    SafelyDeleteFile(currentLocalStderrPath);
                }
                batchResponse.TestCaseResults.Add(tcResult);
            } // End foreach testcase

            // Cleanup compiled executable and original source code file
            SafelyDeleteFile(localCodePath);
            SafelyDeleteFile(localExePath);
            SafelyDeleteFile(localCompilerLogPath);

            return batchResponse;
        }
        
        private async Task<(int ExitCode, string StdOut, string StdErr, long DurationMs, bool TimedOut)> RunProcessAsync(
                    string command, string args, string workingDir, string? stdInPath, int timeoutSeconds)
        {
            var processStartInfo = new ProcessStartInfo(command, args)
            {
                RedirectStandardInput = stdInPath != null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDir,
                // Set Encoding if needed, defaults usually fine
                // StandardOutputEncoding = Encoding.UTF8,
                // StandardErrorEncoding = Encoding.UTF8
            };

            var stdOutBuilder = new StringBuilder();
            var stdErrBuilder = new StringBuilder();
            var stopwatch = Stopwatch.StartNew();
            int exitCode = -999; // Default indicating failure to get code
            bool processTimedOut = false;

            using (var process = new Process { StartInfo = processStartInfo })
            {
                process.OutputDataReceived += (s, e) => { if (e.Data != null) stdOutBuilder.AppendLine(e.Data); };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) stdErrBuilder.AppendLine(e.Data); };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Handle stdin redirection
                if (processStartInfo.RedirectStandardInput && stdInPath != null)
                {
                    StreamWriter stdinWriter = process.StandardInput;
                    stdinWriter.AutoFlush = true;
                    using (var inputFileStream = File.OpenRead(stdInPath))
                    {
                        await inputFileStream.CopyToAsync(stdinWriter.BaseStream);
                    }
                    stdinWriter.Close();
                }

                // Wait for exit with timeout
                bool exited = false;
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
                {
                    try
                    {
                        // Wait for the process to exit OR for the token to be cancelled
                        await process.WaitForExitAsync(cts.Token);
                        exited = true; // Process finished before timeout
                    }
                    catch (OperationCanceledException) // Catches cancellation from cts.Token
                    {
                        // Timeout occurred
                        processTimedOut = true;
                        _logger.LogWarning("Process (PID: {ProcessId}) exceeded timeout of {Timeout}s. Killing.", process.Id, timeoutSeconds);
                        try
                        {
                            process.Kill(entireProcessTree: true); // Force kill the timed-out process
                        }
                        catch (Exception killEx) { _logger.LogError(killEx, "Failed to kill timed out process (PID: {ProcessId}).", process.Id); }
                        exitCode = -1; // Indicate killed by timeout
                    }
                }

                stopwatch.Stop();

                if (!exited)
                {
                    _logger.LogWarning("Process '{Command} {Args}' exceeded timeout of {Timeout}s. Killing.", command, args, timeoutSeconds);
                    processTimedOut = true;
                    try { process.Kill(entireProcessTree: true); } catch (Exception killEx) { _logger.LogError(killEx, "Failed to kill timed out process."); }
                    exitCode = -1; // Indicate killed by timeout
                }
                else
                {
                    exitCode = process.ExitCode;
                    // Special check if using 'timeout' command wrapper
                    if (command == "timeout" && (exitCode == 124 || exitCode == 137))
                    {
                        processTimedOut = true;
                        exitCode = -1; // Standardize timeout indication
                    }
                }
            } // Process disposed

            return (exitCode, stdOutBuilder.ToString(), stdErrBuilder.ToString(), stopwatch.ElapsedMilliseconds, processTimedOut);
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