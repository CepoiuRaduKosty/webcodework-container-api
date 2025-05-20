namespace WebCodeWorkExecutor.Services
{
    public interface IProcessRunner
    {
        Task<(int ExitCode, string StdOut, string StdErr, long DurationMs, bool TimedOut, bool MemoryLimitExceeded)> RunProcessAsync(
                    string command, string args, string workingDir, string? stdInPath, int timeoutSeconds, int maxMemoryMB);
    }
}