using System.Diagnostics;
using System.Text;
using WebCodeWorkExecutor.Services;

namespace GenericRunnerApi.Services
{
    public class LinuxProcessRunner : IProcessRunner
    { 
        private readonly ILogger<LinuxProcessRunner> _logger;

        public LinuxProcessRunner(ILogger<LinuxProcessRunner> logger)
        {
            _logger = logger;
        }
        public async Task<(int ExitCode, string StdOut, string StdErr, long DurationMs, bool TimedOut, bool MemoryLimitExceeded)> RunProcessAsync(
                    string command, string args, string workingDir, string? stdInPath, int timeoutSeconds, int maxMemoryMB)
        {
            var processStartInfo = new ProcessStartInfo(command, args)
            {
                RedirectStandardInput = stdInPath != null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDir,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            var stdOutBuilder = new StringBuilder();
            var stdErrBuilder = new StringBuilder();
            var stopwatch = Stopwatch.StartNew();
            int exitCode = -999; // Default indicating failure to get code
            bool processTimedOut = false;
            bool memoryLimitExceeded = false;

            using (var process = new Process { StartInfo = processStartInfo })
            {
                process.OutputDataReceived += (s, e) => { if (e.Data != null) stdOutBuilder.AppendLine(e.Data); };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) stdErrBuilder.AppendLine(e.Data); };

                CancellationTokenSource? memoryPollCts = null;
                Task? memoryPollingTask = null;

                process.Start();

                if (processStartInfo.RedirectStandardInput && stdInPath != null)
                {
                    using (var inputFileStream = File.OpenRead(stdInPath))
                    using (var reader = new StreamReader(inputFileStream))
                    {
                        string? lineContent = reader.ReadLine();
                        while (lineContent != null)
                        {
                            process.StandardInput.WriteLine(lineContent);
                            lineContent = reader.ReadLine();
                        }
                        process.StandardInput.Close();
                    }
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                string debugmem = "";
                if (maxMemoryMB > 0) // Only poll if a valid limit is set
                {
                    memoryPollCts = new CancellationTokenSource();
                    long memoryLimitBytes = (long)maxMemoryMB * 1024 * 1024;
                    _logger.LogDebug("Starting memory polling for PID {ProcessId}, Limit: {LimitMB}MB ({LimitBytes} bytes)", process.Id, maxMemoryMB, memoryLimitBytes);

                    memoryPollingTask = Task.Run(async () =>
                    {
                        try
                        {
                            // Give the process a moment to start and allocate initial memory
                            await Task.Delay(100, memoryPollCts.Token);

                            while (!process.HasExited && !memoryPollCts.Token.IsCancellationRequested)
                            {
                                process.Refresh(); // Refresh process info to get current memory usage
                                long currentMemoryUsage = process.WorkingSet64;
                                if (currentMemoryUsage > memoryLimitBytes)
                                {
                                    _logger.LogWarning("Process (PID: {ProcessId}) exceeded memory limit. Usage: {UsageBytes}, Limit: {LimitBytes}. Killing.",
                                        process.Id, currentMemoryUsage, memoryLimitBytes);
                                    memoryLimitExceeded = true; // Set the flag
                                    try
                                    {
                                        if (!process.HasExited) process.Kill(entireProcessTree: true);
                                    }
                                    catch (InvalidOperationException) { /* Process already exited */ }
                                    catch (Exception killEx) { _logger.LogError(killEx, "Failed to kill memory-exceeding process (PID: {ProcessId}).", process.Id); }
                                    break; // Exit polling loop
                                }
                                await Task.Delay(250, memoryPollCts.Token); // Poll interval (e.g., every 250ms)
                            }
                        }
                        catch (OperationCanceledException) { _logger.LogDebug("Memory polling cancelled for PID {ProcessId}.", process.Id); }
                        catch (InvalidOperationException ex) when (ex.Message.Contains("No process is associated") || ex.Message.Contains("Process has exited"))
                        {
                            _logger.LogDebug(ex, "Memory polling: Process (PID: {ProcessId}) exited during memory check.", process.Id);
                        }
                        catch (Exception ex) { _logger.LogError(ex, "Unexpected error in memory polling task for PID {ProcessId}.", process.Id); }
                        _logger.LogDebug("Memory polling task finished for PID {ProcessId}.", process.Id);
                    }, memoryPollCts.Token);
                }

                // Wait for exit with timeout
                bool exited = false;
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
                {
                    try
                    {
                        // Wait for the process to exit OR for the token to be cancelled
                        await process.WaitForExitAsync(cts.Token);
                        exited = !memoryLimitExceeded; // Process finished before timeout
                    }
                    catch (OperationCanceledException) // Catches cancellation from cts.Token
                    {
                        if (!memoryLimitExceeded) // Only set timeout if MLE didn't already occur
                        {
                            processTimedOut = true;
                            _logger.LogWarning("Process (PID: {ProcessId}) exceeded time limit of {Timeout}s. Killing.", process.Id, timeoutSeconds);
                            try
                            {
                                if (!process.HasExited) process.Kill(entireProcessTree: true);
                            }
                            catch (InvalidOperationException) { /* Process already exited */ }
                            catch (Exception killEx) { _logger.LogError(killEx, "Failed to kill timed-out process (PID: {ProcessId}).", process.Id); }
                        }
                        exitCode = -1; // Indicate killed by timeout
                    }
                }

                stopwatch.Stop();

                if (memoryPollCts != null)
                {
                    memoryPollCts.Cancel(); // Signal cancellation
                    if (memoryPollingTask != null)
                    {
                        try { await memoryPollingTask; } // Await its completion
                        catch (OperationCanceledException) { /* Expected */ }
                        catch (Exception ex) { _logger.LogError(ex, "Error during memory polling task cleanup for PID {ProcessId}.", process.Id); }
                    }
                    memoryPollCts.Dispose();
                }

                if (memoryLimitExceeded)
                {
                    exitCode = -2; // Specific code for MLE
                    processTimedOut = false; // MLE takes precedence
                    exited = false;
                }
                else if (!exited)
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
            }

            return (exitCode, stdOutBuilder.ToString(), stdErrBuilder.ToString(), stopwatch.ElapsedMilliseconds, processTimedOut, memoryLimitExceeded);
        }

    }
}