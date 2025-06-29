using System.Diagnostics;
using System.Text;
using WebCodeWorkExecutor.Services;

namespace GenericRunnerApi.Services
{
    public class LinuxProcessRunner : IProcessRunner
    { 
        private readonly ILogger<LinuxProcessRunner> _logger;
        private readonly IConfiguration _config;

        public LinuxProcessRunner(ILogger<LinuxProcessRunner> logger, IConfiguration configuration)
        {
            _logger = logger;
            _config = configuration;
        }
        public async Task<(int ExitCode, string StdOut, string StdErr, long DurationMs, bool TimedOut, bool MemoryLimitExceeded)> RunProcessAsync(
                    string command, string args, string workingDir, string? stdin, int timeoutSeconds, int maxMemoryMB)
        {
            var processStartInfo = new ProcessStartInfo(command, args)
            {
                RedirectStandardInput = stdin != null,
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
            int exitCode = -999; 
            bool processTimedOut = false;
            bool memoryLimitExceeded = false;

            using (var process = new Process { StartInfo = processStartInfo })
            {
                process.OutputDataReceived += (s, e) => { if (e.Data != null) stdOutBuilder.AppendLine(e.Data); };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) stdErrBuilder.AppendLine(e.Data); };

                CancellationTokenSource? memoryPollCts = null;
                Task? memoryPollingTask = null;

                process.Start();

                if (processStartInfo.RedirectStandardInput && stdin != null)
                {
                    await process.StandardInput.WriteAsync(stdin);
                    process.StandardInput.Close();
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                memoryPollCts = new CancellationTokenSource();
                long memoryLimitBytes = (long)maxMemoryMB * 1024 * 1024;
                long memoryLimitGlobalBytes = (long)_config.GetValue<int>("GlobalLimits:MaxMemoryMb") * 1024 * 1024;
                _logger.LogDebug("Starting memory polling for PID {ProcessId}, Limit: {LimitMB}MB ({LimitBytes} bytes)", process.Id, maxMemoryMB, memoryLimitBytes);

                memoryPollingTask = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(100, memoryPollCts.Token);

                        while (!process.HasExited && !memoryPollCts.Token.IsCancellationRequested)
                        {
                            process.Refresh(); 
                            long currentMemoryUsage = process.WorkingSet64;
                            if (currentMemoryUsage > memoryLimitBytes || currentMemoryUsage > memoryLimitGlobalBytes)
                            {
                                _logger.LogWarning("Process (PID: {ProcessId}) exceeded memory limit. Usage: {UsageBytes}, Limit: {LimitBytes}. Killing.",
                                    process.Id, currentMemoryUsage, memoryLimitBytes);
                                memoryLimitExceeded = true; 
                                try
                                {
                                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                                }
                                catch (InvalidOperationException) { /* Process already exited */ }
                                catch (Exception killEx) { _logger.LogError(killEx, "Failed to kill memory-exceeding process (PID: {ProcessId}).", process.Id); }
                                break; 
                            }
                            await Task.Delay(250, memoryPollCts.Token); 
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

                bool exited = false;
                int timeoutGlobalSeconds = _config.GetValue<int>("GlobalLimits:MaxTimeSec");
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds < timeoutGlobalSeconds ? timeoutSeconds : timeoutGlobalSeconds)))
                {
                    try
                    {
                        await process.WaitForExitAsync(cts.Token);
                        exited = !memoryLimitExceeded;
                    }
                    catch (OperationCanceledException)
                    {
                        if (!memoryLimitExceeded)
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
                        exitCode = -1;
                    }
                }

                stopwatch.Stop();

                if (memoryPollCts != null)
                {
                    memoryPollCts.Cancel(); 
                    if (memoryPollingTask != null)
                    {
                        try { await memoryPollingTask; } 
                        catch (OperationCanceledException) { /* Expected */ }
                        catch (Exception ex) { _logger.LogError(ex, "Error during memory polling task cleanup for PID {ProcessId}.", process.Id); }
                    }
                    memoryPollCts.Dispose();
                }

                if (memoryLimitExceeded)
                {
                    exitCode = -2; 
                    processTimedOut = false; 
                    exited = false;
                }
                else if (!exited)
                {
                    _logger.LogWarning("Process '{Command} {Args}' exceeded timeout of {Timeout}s. Killing.", command, args, timeoutSeconds);
                    processTimedOut = true;
                    try { process.Kill(entireProcessTree: true); } catch (Exception killEx) { _logger.LogError(killEx, "Failed to kill timed out process."); }
                    exitCode = -1; 
                }
                else
                {
                    exitCode = process.ExitCode;
                    
                    if (command == "timeout" && (exitCode == 124 || exitCode == 137))
                    {
                        processTimedOut = true;
                        exitCode = -1; 
                    }
                }
            }

            return (exitCode, stdOutBuilder.ToString(), stdErrBuilder.ToString(), stopwatch.ElapsedMilliseconds, processTimedOut, memoryLimitExceeded);
        }

    }
}