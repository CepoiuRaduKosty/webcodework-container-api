// Services/CExecutionLogic.cs
using GenericRunnerApi.Dtos;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks; // For Task

namespace GenericRunnerApi.Services
{
    public class CExecutionLogic : ICodeExecutionLogic
    {
        private readonly ILogger<CExecutionLogic> _logger;

        public CExecutionLogic(ILogger<CExecutionLogic> logger)
        {
            _logger = logger;
        }

        public async Task<CompileResponse> CompileAsync(CompileRequest request, string workingDirectory)
        {
            var codeFilePath = Path.Combine(workingDirectory, request.CodeFileName);
            var outputExePath = Path.Combine(workingDirectory, request.OutputExecutableName);
            var compilerLogPath = Path.Combine(workingDirectory, "compiler.log"); // Optional log file

            if (!File.Exists(codeFilePath))
            {
                return new CompileResponse { Success = false, ErrorMessage = $"Code file not found: {request.CodeFileName}" };
            }

            // Delete previous executable/log if they exist
            if (File.Exists(outputExePath)) File.Delete(outputExePath);
            if (File.Exists(compilerLogPath)) File.Delete(compilerLogPath);

            // Arguments for GCC
            // -o : output file
            // -O2: optimization level
            // -Wall: enable all warnings
            // -lm : link math library
            string args = $"\"{codeFilePath}\" -o \"{outputExePath}\" -O2 -Wall -lm";

            var processStartInfo = new ProcessStartInfo("gcc", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory
            };

            _logger.LogInformation("Executing compilation: gcc {Arguments}", args);
            string compilerOutput = "";
            bool success = false;

            try
            {
                using (var process = Process.Start(processStartInfo))
                {
                    if (process == null) throw new Exception("Failed to start compiler process.");

                    // Read output/error streams asynchronously
                    var outputTask = process.StandardOutput.ReadToEndAsync();
                    var errorTask = process.StandardError.ReadToEndAsync();

                    await process.WaitForExitAsync(); // Wait for the process to complete

                    compilerOutput = $"Stdout:\n{(await outputTask).Trim()}\nStderr:\n{(await errorTask).Trim()}".Trim();
                    success = process.ExitCode == 0;

                    _logger.LogInformation("Compilation finished. Exit Code: {ExitCode}", process.ExitCode);
                     if (!success) _logger.LogWarning("Compilation failed. Output:\n{Output}", compilerOutput);

                     // Optionally write compiler output to log file
                     // await File.WriteAllTextAsync(compilerLogPath, compilerOutput);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception during compilation.");
                return new CompileResponse { Success = false, Output = compilerOutput, ErrorMessage = $"Compilation failed: {ex.Message}" };
            }

            return new CompileResponse { Success = success, Output = compilerOutput };
        }


        public async Task<RunResponse> RunAsync(RunRequest request, string workingDirectory)
        {
            var exePath = Path.Combine(workingDirectory, request.ExecutablePath);
            var inputFilePath = !string.IsNullOrEmpty(request.InputFileName) ? Path.Combine(workingDirectory, request.InputFileName) : null;

            if (!File.Exists(exePath))
                return new RunResponse { Status = RunStatus.InternalError, ErrorMessage = $"Executable not found: {request.ExecutablePath}" };
            if (inputFilePath != null && !File.Exists(inputFilePath))
                 return new RunResponse { Status = RunStatus.InternalError, ErrorMessage = $"Input file not found: {request.InputFileName}" };


            // Use timeout command for reliable time limiting
            string command = "timeout";
            string args = $"--signal=SIGKILL {request.TimeLimitSeconds}s \"{exePath}\""; // Kill after timeout

            var processStartInfo = new ProcessStartInfo(command, args)
            {
                RedirectStandardInput = true, // Redirect stdin
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory
            };

             _logger.LogInformation("Executing run: {Command} {Arguments}", command, args);
             var stdOutBuilder = new StringBuilder();
             var stdErrBuilder = new StringBuilder();
             var stopwatch = Stopwatch.StartNew();
             int exitCode = -1;
             bool timedOut = false;


            try
            {
                using (var process = new Process { StartInfo = processStartInfo })
                {
                    // Attach handlers to capture output asynchronously
                    process.OutputDataReceived += (sender, e) => { if (e.Data != null) stdOutBuilder.AppendLine(e.Data); };
                    process.ErrorDataReceived += (sender, e) => { if (e.Data != null) stdErrBuilder.AppendLine(e.Data); };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    // Write input to stdin if provided
                    if (inputFilePath != null)
                    {
                        using (var inputFileStream = File.OpenRead(inputFilePath))
                        {
                            await inputFileStream.CopyToAsync(process.StandardInput.BaseStream);
                        }
                        // Close the input stream to signal EOF to the process
                        process.StandardInput.Close();
                    }

                    // Wait for exit with timeout (managed by the 'timeout' command now)
                    await process.WaitForExitAsync();
                    exitCode = process.ExitCode;
                    stopwatch.Stop();

                    _logger.LogInformation("Execution finished. Exit Code: {ExitCode}, Duration: {Duration}ms", exitCode, stopwatch.ElapsedMilliseconds);

                     // Check if timeout occurred (exit code 124 or 137 from timeout command)
                    if (exitCode == 124 || exitCode == 137)
                    {
                        timedOut = true;
                        _logger.LogWarning("Process timed out or was killed.");
                    } else if (exitCode != 0) {
                         _logger.LogWarning("Process exited with non-zero code {ExitCode}.", exitCode);
                    }
                }
            }
             catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex, "Exception during execution.");
                return new RunResponse { Status = RunStatus.InternalError, DurationMs = stopwatch.ElapsedMilliseconds, ErrorMessage = $"Execution failed: {ex.Message}" };
            }


             // Determine final status
             string status = RunStatus.InternalError; // Default
             if(timedOut) status = RunStatus.TimeLimitExceeded;
             else if (exitCode != 0) status = RunStatus.RuntimeError;
             else status = RunStatus.Success; // Succeeded if exited normally with code 0

            return new RunResponse
            {
                Status = status,
                Stdout = stdOutBuilder.ToString().TrimEnd('\r', '\n'), // Trim trailing newlines
                Stderr = stdErrBuilder.ToString().TrimEnd('\r', '\n'),
                ExitCode = exitCode,
                DurationMs = stopwatch.ElapsedMilliseconds
            };
        }
    }
}