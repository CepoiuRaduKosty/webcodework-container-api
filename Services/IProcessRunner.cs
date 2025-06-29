namespace WebCodeWorkExecutor.Services
{
    public interface IProcessRunner
    {
        Task<(int ExitCode, string StdOut, string StdErr, long DurationMs, bool TimedOut, bool MemoryLimitExceeded)> RunProcessAsync(
                    string command, string args, string workingDir, string? stdin, int timeoutSeconds, int maxMemoryMB);
    }
}