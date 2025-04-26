// Dtos/ExecutionDtos.cs
using System.ComponentModel.DataAnnotations;

namespace GenericRunnerApi.Dtos
{
    /// <summary>
    /// Request body for the /compile endpoint.
    /// </summary>
    public class CompileRequest
    {
        /// <summary>
        /// The name of the source code file (e.g., "solution.c") relative to the working directory.
        /// </summary>
        [Required]
        public string CodeFileName { get; set; } = "solution.c";

        /// <summary>
        /// The desired name for the output executable file (e.g., "solution") relative to the working directory.
        /// </summary>
        [Required]
        public string OutputExecutableName { get; set; } = "solution";
    }

    /// <summary>
    /// Response body from the /compile endpoint.
    /// </summary>
    public class CompileResponse
    {
        /// <summary>
        /// Indicates if the compilation was successful (ExitCode == 0).
        /// </summary>
        public bool Success { get; set; }
        public string? Output { get; set; }

        /// <summary>
        /// Contains details if an internal error occurred during the compilation setup or process execution itself.
        /// </summary>
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// Request body for the /run endpoint.
    /// </summary>
    public class RunRequest
    {
        /// <summary>
        /// The path to the executable file to run (e.g., "solution") relative to the working directory.
        /// </summary>
        [Required]
        public string ExecutablePath { get; set; } = "solution";

        /// <summary>
        /// Optional: The name of the file containing standard input data (e.g., "input.txt") relative to the working directory.
        /// If null or empty, the program runs without piped stdin.
        /// </summary>
        public string? InputFileName { get; set; } = "input.txt";

        /// <summary>
        /// Time limit for the execution in seconds.
        /// </summary>
        [Range(1, 30)] // Example range constraint
        public int TimeLimitSeconds { get; set; } = 5;
    }

    /// <summary>
    /// Response body from the /run endpoint.
    /// </summary>
    public class RunResponse
    {
        /// <summary>
        /// The final status of the execution (e.g., SUCCESS, RUNTIME_ERROR). See RunStatus constants.
        /// </summary>
        [Required]
        public string Status { get; set; } = RunStatus.InternalError;

        /// <summary>
        /// The standard output captured from the executed program.
        /// </summary>
        public string? Stdout { get; set; }

        /// <summary>
        /// The standard error captured from the executed program.
        /// </summary>
        public string? Stderr { get; set; }

        /// <summary>
        /// The exit code returned by the executed program (or the timeout process).
        /// </summary>
        public int? ExitCode { get; set; }

        /// <summary>
        /// The approximate duration of the execution in milliseconds.
        /// </summary>
        public long? DurationMs { get; set; }

        /// <summary>
        /// Contains details if an internal error occurred during the execution setup or process management.
        /// </summary>
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// Defines constant strings for the execution status reported by the /run endpoint.
    /// </summary>
    public static class RunStatus
    {
        public const string Success = "SUCCESS"; // Program ran successfully (ExitCode 0) and completed within time limit. Output comparison happens later.
        public const string RuntimeError = "RUNTIME_ERROR"; // Program exited with a non-zero exit code.
        public const string TimeLimitExceeded = "TIME_LIMIT_EXCEEDED"; // Program exceeded the time limit.
        public const string InternalError = "INTERNAL_ERROR"; // Runner API itself encountered an error setting up or running the process.
        // Add others like MemoryLimitExceeded if needed later
    }
}