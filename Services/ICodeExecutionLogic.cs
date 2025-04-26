// Services/ICodeExecutionLogic.cs
using WebCodeWorkExecutor.Dtos; // Assuming DTOs are in this namespace

namespace WebCodeWorkExecutor.Services
{
    public interface ICodeEvaluationLogic
    {
        /// <summary>
        /// Compiles, runs, compares, and returns the final evaluation result
        /// using the provided file contents. Assumes files will be written
        /// to the workingDirectory by the caller if needed by underlying tools.
        /// </summary>
        /// <param name="codeContent">The source code content.</param>
        /// <param name="inputContent">The standard input content.</param>
        /// <param name="expectedOutputContent">The expected standard output content.</param>
        /// <param name="timeLimitSeconds">Execution time limit.</param>
        /// <param name="workingDirectory">The local directory to perform work in.</param>
        /// <returns>The final evaluation response.</returns>
        Task<ExecuteResponse> EvaluateAsync(
            string codeContent,
            string inputContent,
            string expectedOutputContent,
            int timeLimitSeconds,
            string workingDirectory
            // Add memoryLimitMB later if needed
            );
    }
}