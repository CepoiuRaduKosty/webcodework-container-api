// Dtos/ExecutionDtos.cs
using System.ComponentModel.DataAnnotations;

namespace WebCodeWorkContainer.Dtos
{
    // Request to compile code
    public class CompileRequest
    {
        [Required]
        public string CodeFileName { get; set; } = "solution.c"; // Relative to sandbox

        [Required]
        public string OutputExecutableName { get; set; } = "solution"; // Relative to sandbox
    }

    // Response from compilation
    public class CompileResponse
    {
        public bool Success { get; set; }
        public string? Output { get; set; } // Compiler stdout/stderr
        public string? ErrorMessage { get; set; } // For internal errors during compilation setup
    }

    // Request to run the compiled code
    public class RunRequest
    {
        [Required]
        public string ExecutablePath { get; set; } = "solution"; // Relative to sandbox

        public string? InputFileName { get; set; } = "input.txt"; // Relative to sandbox, null if no stdin needed

        [Range(1, 30)] // Example limit
        public int TimeLimitSeconds { get; set; } = 5; // Default time limit

        // Add MemoryLimitMB if needed later
    }

    // Response from running code
    public class RunResponse
    {
        [Required]
        public string Status { get; set; } = RunStatus.InternalError; // Default status

        public string? Stdout { get; set; }
        public string? Stderr { get; set; }
        public int? ExitCode { get; set; }
        public long? DurationMs { get; set; }
        public string? ErrorMessage { get; set; } // For internal errors during execution setup
    }

    // Static class for status constants
    public static class RunStatus
    {
        public const string Success = "SUCCESS";
        public const string RuntimeError = "RUNTIME_ERROR";
        public const string TimeLimitExceeded = "TIME_LIMIT_EXCEEDED";
        public const string InternalError = "INTERNAL_ERROR";
        // Add more as needed (e.g., MemoryLimitExceeded)
    }
}