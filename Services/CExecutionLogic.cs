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

        public async Task<ExecuteResponse> EvaluateAsync(
            string codeContent,
            string inputContent,
            string expectedOutputContent,
            int timeLimitSeconds,
            string workingDirectory
            // int memoryLimitMB = 256 // Add later
            )
        {
            // Define local filenames within the working directory
            string codeFileName = "solution.c";
            string inputFileName = "input.txt";
            string expectedOutputFileName = "expected_output.txt";
            string outputExeName = "solution";

            string localCodePath = Path.Combine(workingDirectory, codeFileName);
            string localInputPath = Path.Combine(workingDirectory, inputFileName);
            string localExpectedOutputPath = Path.Combine(workingDirectory, expectedOutputFileName);
            string localExePath = Path.Combine(workingDirectory, outputExeName);
            string localStdoutPath = Path.Combine(workingDirectory, "program.stdout");
            string localStderrPath = Path.Combine(workingDirectory, "program.stderr");
            string localCompilerLogPath = Path.Combine(workingDirectory, "compiler.log");

            try
            {
                // --- 1. Write Received Content to Local Files ---
                _logger.LogDebug("Writing content to local files in {WorkingDirectory}...", workingDirectory);
                // Use Task.WhenAll for parallel writing
                var writeTasks = new[] {
                     File.WriteAllTextAsync(localCodePath, codeContent, Encoding.UTF8),
                     File.WriteAllTextAsync(localInputPath, inputContent, Encoding.UTF8),
                     File.WriteAllTextAsync(localExpectedOutputPath, expectedOutputContent, Encoding.UTF8)
                 };
                await Task.WhenAll(writeTasks);
                _logger.LogDebug("Finished writing content to local files.");


                // --- 2. Compile (Logic remains the same, uses local paths) ---
                _logger.LogInformation("Compiling {CodeFile}...", codeFileName);
                if (File.Exists(localExePath)) File.Delete(localExePath);
                if (File.Exists(localCompilerLogPath)) File.Delete(localCompilerLogPath);
                string compileArgs = $"\"{localCodePath}\" -o \"{localExePath}\" -O2 -Wall -lm";
                var (compileExitCode, compileStdOut, compileStdErr, __, ___) = await RunProcessAsync("gcc", compileArgs, workingDirectory, null, 15); // 15s compile timeout
                string compilerOutput = $"Stdout:\n{compileStdOut}\nStderr:\n{compileStdErr}".Trim();
                if (compileExitCode != 0)
                {
                    // Log the warning/error with details
                    _logger.LogWarning("Compilation failed for {CodeFile}. Exit Code: {ExitCode}. Output:\n{CompilerOutput}",
                        codeFileName, compileExitCode, compilerOutput);

                    // Return immediately with CompileError status and the captured output
                    return new ExecuteResponse
                    {
                        Status = EvaluationStatus.CompileError,
                        CompilerOutput = compilerOutput, // Include compiler messages
                        // Other fields remain null as execution/comparison didn't happen
                        Stdout = null,
                        Stderr = null,
                        DurationMs = null,
                        Message = "Compilation failed." // Optional concise message
                    };
                }
                _logger.LogInformation("Compilation successful.");


                // --- 3. Execute (Logic remains the same, uses local paths) ---
                _logger.LogInformation("Executing {ExePath}...", outputExeName);
                if (File.Exists(localStdoutPath)) File.Delete(localStdoutPath);
                if (File.Exists(localStderrPath)) File.Delete(localStderrPath);
                string runCommand = "timeout";
                string runArgs = $"--signal=SIGKILL {timeLimitSeconds}s \"{localExePath}\"";
                var (runExitCode, runStdOut, runStdErr, durationMs, timedOut) = await RunProcessAsync(
                    runCommand, runArgs, workingDirectory, localInputPath, timeLimitSeconds + 2);
                await File.WriteAllTextAsync(localStdoutPath, runStdOut); // Save actual stdout
                await File.WriteAllTextAsync(localStderrPath, runStdErr); // Save actual stderr
                _logger.LogInformation("Execution finished. Exit Code: {ExitCode}, TimedOut: {TimedOut}, Duration: {Duration}ms", runExitCode, timedOut, durationMs);
                if (timedOut) return new ExecuteResponse { Status = EvaluationStatus.TimeLimitExceeded, DurationMs = durationMs, Stdout = runStdOut, Stderr = runStdErr };
                if (runExitCode != 0) return new ExecuteResponse { Status = EvaluationStatus.RuntimeError, DurationMs = durationMs, Stdout = runStdOut, Stderr = runStdErr };


                // --- 4. Compare Output (Logic remains the same, uses local path/variable) ---
                _logger.LogInformation("Comparing output...");
                string actualOutput = runStdOut; // Use captured stdout directly
                                                 // Expected output is already in expectedOutputContent variable passed to the method
                if (CompareOutputs(actualOutput, expectedOutputContent))
                {
                    // Outputs match
                    _logger.LogInformation("Evaluation result: ACCEPTED");
                    return new ExecuteResponse
                    {
                        Status = EvaluationStatus.Accepted,
                        Stdout = actualOutput, // Include actual output
                        Stderr = runStdErr,    // Include stderr (might be empty)
                        DurationMs = durationMs, // Include duration
                        // CompilerOutput, Message, ExitCode are typically null/irrelevant for ACCEPTED
                    };
                }
                else
                {
                    // Outputs do not match
                    _logger.LogInformation("Evaluation result: WRONG_ANSWER");
                    return new ExecuteResponse
                    {
                        Status = EvaluationStatus.WrongAnswer,
                        Stdout = actualOutput, // Include the INCORRECT actual output for debugging
                        Stderr = runStdErr,    // Include stderr
                        DurationMs = durationMs, // Include duration
                        Message = "Output did not match expected output." // Optional message
                                                                          // CompilerOutput, ExitCode are null/irrelevant here
                    };
                }
            }
            catch (FileNotFoundException fnfEx) // Catch local file issues
            {
                _logger.LogError(fnfEx, "File error during evaluation logic execution.");
                return new ExecuteResponse { Status = EvaluationStatus.InternalError, Message = $"Internal error: {fnfEx.Message}" }; // Treat local file issues as internal
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled internal error during evaluation logic.");
                return new ExecuteResponse { Status = EvaluationStatus.InternalError, Message = $"Internal evaluation error: {ex.Message}" };
            }
            finally
            {
                // Clean up local files written by this method
                SafelyDeleteFile(localCodePath);
                SafelyDeleteFile(localInputPath);
                SafelyDeleteFile(localExpectedOutputPath);
                SafelyDeleteFile(localExePath);
                SafelyDeleteFile(localStdoutPath);
                SafelyDeleteFile(localStderrPath);
                SafelyDeleteFile(localCompilerLogPath);
            }
        }

        private async Task<(int ExitCode, string StdOut, string StdErr, long DurationMs, bool TimedOut)> RunProcessAsync(
                    string command, string args, string workingDir, string? stdInPath, int timeoutSeconds)
        {
            var processStartInfo = new ProcessStartInfo(command, args)
            {
                RedirectStandardInput = stdInPath != null, // Only redirect if path is provided
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
                    using (var inputFileStream = File.OpenRead(stdInPath))
                    {
                        await inputFileStream.CopyToAsync(process.StandardInput.BaseStream);
                    }
                    process.StandardInput.Close(); // Must close stdin to signal EOF
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
                        catch (Exception killEx) { _logger.LogError(killEx, "Failed to kill timed out process (PID: {ProcessId}).", process.Id);}
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