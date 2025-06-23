
using System.ComponentModel.DataAnnotations;

namespace WebCodeWorkExecutor.Dtos
{
    public class BatchTestCaseItem
    {
        [Required]
        public string InputFilePath { get; set; } = string.Empty;

        [Required]
        public string ExpectedOutputFilePath { get; set; } = string.Empty;

        [Required]
        [Range(100, 10000)] 
        public int TimeLimitMs { get; set; } = 2000;

        [Required]
        [Range(32, 512)]  
        public int MaxRamMB { get; set; } = 128;

        public string? TestCaseId { get; set; } 
    }

    public class BatchExecuteRequest
    {
        [Required]
        public string Language { get; set; } = "c"; 

        public string? Version { get; set; } 

        [Required]
        public string CodeFilePath { get; set; } = string.Empty;

        [Required]
        [MinLength(1, ErrorMessage = "At least one test case must be provided.")]
        public List<BatchTestCaseItem> TestCases { get; set; } = new List<BatchTestCaseItem>();
    }

    public class TestCaseResult
    {
        public string? TestCaseId { get; set; }
        [Required]
        public string Status { get; set; } = EvaluationStatus.InternalError;
        public string? Stdout { get; set; }
        public string? Stderr { get; set; }
        public long? DurationMs { get; set; }
        public string? Message { get; set; }
        public int? ExitCode { get; set; }
        public bool MaximumMemoryException { get; set; }
    }

    public class BatchExecuteResponse
    {
        [Required]
        public bool CompilationSuccess { get; set; }
        public string? CompilerOutput { get; set; }
        public List<TestCaseResult> TestCaseResults { get; set; } = new List<TestCaseResult>();
    }

    public static class EvaluationStatus
    {
        public const string Accepted = "ACCEPTED";
        public const string WrongAnswer = "WRONG_ANSWER";
        public const string CompileError = "COMPILE_ERROR";
        public const string RuntimeError = "RUNTIME_ERROR";
        public const string TimeLimitExceeded = "TIME_LIMIT_EXCEEDED";
        public const string MemoryLimitExceeded = "MEMORY_LIMIT_EXCEEDED";
        public const string FileError = "FILE_ERROR"; 
        public const string InternalError = "INTERNAL_ERROR";
    }
}