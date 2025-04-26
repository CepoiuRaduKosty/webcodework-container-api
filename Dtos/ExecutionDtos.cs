// Dtos/EvaluationDtos.cs
using System.ComponentModel.DataAnnotations;

namespace WebCodeWorkExecutor.Dtos
{
    /// <summary>
    /// Request for the single evaluation endpoint.
    /// Contains paths to files stored in configured blob storage.
    /// </summary>
    public class ExecuteRequest
    {
        [Required]
        public string Language { get; set; } = "c"; // Keep language info if needed internally

        [Required]
        public string CodeFilePath { get; set; } = string.Empty; // e.g., "submissions/123/solution.c"

        [Required]
        public string InputFilePath { get; set; } = string.Empty; // e.g., "testcases/10/input/abc.in"

        [Required]
        public string ExpectedOutputFilePath { get; set; } = string.Empty; // e.g., "testcases/10/output/abc.out"

        [Range(1, 30)]
        public int TimeLimitSeconds { get; set; } = 5;

        // public int MemoryLimitMB { get; set; } = 256; // Add later if needed
    }

    /// <summary>
    /// Response from the single evaluation endpoint.
    /// Contains the final verdict and relevant output/errors.
    /// </summary>
    public class ExecuteResponse
    {
        [Required]
        public string Status { get; set; } = EvaluationStatus.InternalError; // Final Verdict

        public string? CompilerOutput { get; set; } // Output from compilation step
        public string? Stdout { get; set; }         // Actual standard output from execution
        public string? Stderr { get; set; }         // Actual standard error from execution
        public string? Message { get; set; }        // Additional info/error details from runner
        public long? DurationMs { get; set; }       // Execution duration
    }

    /// <summary>
    /// Defines constant strings for final evaluation status.
    /// </summary>
    public static class EvaluationStatus
    {
        public const string Accepted = "ACCEPTED";
        public const string WrongAnswer = "WRONG_ANSWER";
        public const string CompileError = "COMPILE_ERROR";
        public const string RuntimeError = "RUNTIME_ERROR";
        public const string TimeLimitExceeded = "TIME_LIMIT_EXCEEDED";
        // public const string MemoryLimitExceeded = "MEMORY_LIMIT_EXCEEDED";
        public const string FileError = "FILE_ERROR"; // Error fetching files
        public const string InternalError = "INTERNAL_ERROR";
    }
}