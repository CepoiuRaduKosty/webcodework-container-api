// Program.cs
using Microsoft.AspNetCore.Authentication;
using WebCodeWorkExecutor.Authentication; // Your auth namespace
using Microsoft.OpenApi.Models;
using Azure.Storage.Blobs;
using GenericRunnerApi.Services;
using WebCodeWorkExecutor.Dtos;
using WebCodeWorkExecutor.Services;
using Azure.Storage.Blobs.Models;
using System.Text;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.json",
        optional: false,
        reloadOnChange: true);

// --- Configuration ---
// Language is read from config/env vars later
var configuredLanguage = builder.Configuration.GetValue<string>("Execution:Language")?.ToLowerInvariant() ?? "c";
var workingDirectory = builder.Configuration.GetValue<string>("Execution:WorkingDirectory") ?? "/sandbox";

if (string.IsNullOrEmpty(configuredLanguage))
{
    throw new InvalidOperationException("Execution:Language configuration is required.");
}


// Ensure working directory exists (important if running locally without container)
if (!Directory.Exists(workingDirectory)) Directory.CreateDirectory(workingDirectory);


// --- Services ---
builder.Services.AddProblemDetails();

// Authentication & Authorization
builder.Services.AddAuthentication(ApiKeyAuthenticationDefaults.AuthenticationScheme)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationDefaults.AuthenticationScheme, o => { });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("ApiKeyPolicy", policy =>
    {
        policy.AddAuthenticationSchemes(ApiKeyAuthenticationDefaults.AuthenticationScheme);
        policy.RequireAuthenticatedUser();
    });
});

// Swagger / OpenAPI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = $"Runner API ({configuredLanguage})", Version = "v1" });
    // Add API Key security definition for Swagger UI
    options.AddSecurityDefinition(ApiKeyAuthenticationDefaults.AuthenticationScheme, new OpenApiSecurityScheme
    {
        In = ParameterLocation.Header, // Expect the API key in the header
        Description = "Please enter the API Key", // Description in Swagger UI
        Name = "X-Api-Key", // The name of the header
        Type = SecuritySchemeType.ApiKey, // Specifies it's an API key
        Scheme = "ApiKeyScheme" // A descriptive scheme name (can be anything)
    });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement {
    {
        new OpenApiSecurityScheme
        {
            Reference = new OpenApiReference
            {
                Type = ReferenceType.SecurityScheme,
                Id = ApiKeyAuthenticationDefaults.AuthenticationScheme // Reference the defined security scheme
            }
        },
        new string[] {} // No specific scopes for API key
    }});
});

builder.Services.AddScoped<ICodeEvaluationLogic, MainEvaluationLogic>();
builder.Services.AddScoped<IProcessRunner, LinuxProcessRunner>();
switch (configuredLanguage)
{
    case "c":
        builder.Services.AddScoped<ILanguageSpecificLogic, CSpecificLogicProvider>();
        break;
    case "python":
        builder.Services.AddScoped<ILanguageSpecificLogic, PythonSpecificLogicProvider>();
        break;
    default:
        var errorMessage = $"Unsupported language configured: '{configuredLanguage}'";
        throw new NotSupportedException(errorMessage);
}

builder.Services.AddSingleton(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var connectionString = configuration.GetValue<string>("AzureStorage:ConnectionString");
    if (string.IsNullOrEmpty(connectionString))
    {
        // Get current working directory
        var cwd = System.Environment.CurrentDirectory;
        // Check if appsettings.json exists in the CWD
        var appSettingsPath = System.IO.Path.Combine(cwd, "appsettings.json");
        var appSettingsExists = System.IO.File.Exists(appSettingsPath);

        throw new InvalidOperationException(
            $"Azure Storage Connection String not configured. " +
            $"Debug: ConnectionString='{configuration.GetValue<string>("AzureStorage:ConnectionString")}', " +
            $"CWD='{cwd}', " +
            $"appsettings.json exists='{appSettingsExists}'"
        );
    }
    return new BlobServiceClient(connectionString);
});
// -----

// --- Application ---
var app = builder.Build();

// --- Middleware ---
//if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/swagger/v1/swagger.json", $"Runner API ({configuredLanguage}) v1"));
}

app.UseExceptionHandler(exceptionHandlerApp =>
{
    exceptionHandlerApp.Run(async context =>
    {
        var exceptionHandlerPathFeature = context.Features.Get<IExceptionHandlerPathFeature>();
        var exception = exceptionHandlerPathFeature?.Error;
        var unhandledLogger = context.RequestServices.GetRequiredService<ILogger<Program>>(); // Get a logger instance

        unhandledLogger.LogError(exception, "Global unhandled exception: {ErrorMessage}", exception?.Message);

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/problem+json";

        var problemDetails = new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = $"Unhandled Exception in Runner: {exception?.GetType().Name}",
            Instance = context.Request.Path,
            Detail = exception?.ToString(), // Full exception details
        };
        await context.Response.WriteAsJsonAsync(problemDetails);
    });
});
// No HTTPS redirection needed for internal API called directly via IP/localhost/container name
// app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();


// --- API Endpoints ---

// Basic health check
app.MapGet("/", () => $"Generic Runner API ({configuredLanguage}) - Running")
    .ExcludeFromDescription(); // Hide from Swagger

app.MapPost("/execute", async (
    BatchExecuteRequest request,        // The new DTO with CodeFilePath and List<BatchTestCaseItem>
    ICodeEvaluationLogic evaluationLogic,
    BlobServiceClient blobServiceClient,
    IConfiguration configuration,
    ILogger<Program> endpointLogger
    ) =>
{
    endpointLogger.LogInformation("Batch execute request received for lang: {Lang}, code: {CodeFile}, TCs: {TCCount}",
    request.Language, request.CodeFilePath, request.TestCases.Count);

    var containerName = configuration.GetValue<string>("AzureStorage:ContainerName");
    if (string.IsNullOrEmpty(containerName))
    {
        endpointLogger.LogCritical("Azure Storage container name not configured.");
        // Return a BatchExecuteResponse with internal error for all test cases
        return Results.Ok(new BatchExecuteResponse
        {
            CompilationSuccess = false,
            CompilerOutput = "Runner internal error: Storage container not configured.",
            TestCaseResults = request.TestCases.Select(tc => new TestCaseResult { TestCaseId = tc.TestCaseId, Status = EvaluationStatus.InternalError, Message = "Storage not configured." }).ToList()
        });
    }
    var containerClient = blobServiceClient.GetBlobContainerClient(containerName);

    string codeContent;
    List<TestCaseEvaluationData> testCasesData = new List<TestCaseEvaluationData>();

    try
    {
        endpointLogger.LogDebug("Fetching code file from: {CodeFilePath}", request.CodeFilePath);
        codeContent = await FetchBlobContentAsync(containerClient, request.CodeFilePath, endpointLogger);
        endpointLogger.LogDebug("Code file fetched successfully.");

        // 2. Fetch Content for Each Test Case
        endpointLogger.LogDebug("Fetching content for {NumTestCases} test cases...", request.TestCases.Count);
        foreach (var tcItem in request.TestCases)
        {
            string inputContent = await FetchBlobContentAsync(containerClient, tcItem.InputFilePath, endpointLogger);
            string expectedOutputContent = await FetchBlobContentAsync(containerClient, tcItem.ExpectedOutputFilePath, endpointLogger);
            testCasesData.Add(new TestCaseEvaluationData(
                InputContent: inputContent,
                ExpectedOutputContent: expectedOutputContent,
                TimeLimitMs: tcItem.TimeLimitMs,
                MaxRamMB: tcItem.MaxRamMB,
                TestCaseId: tcItem.TestCaseId
            ));
        }
        endpointLogger.LogDebug("All test case files fetched successfully.");
    }
    catch (FileNotFoundException ex)
    {
        endpointLogger.LogError(ex, "A required file was not found in blob storage.");
        return Results.Ok(new BatchExecuteResponse
        { // Return a BatchExecuteResponse indicating the error
            CompilationSuccess = false,
            CompilerOutput = $"File error: {ex.Message}",
            TestCaseResults = request.TestCases.Select(tc => new TestCaseResult { TestCaseId = tc.TestCaseId, Status = EvaluationStatus.FileError, Message = $"File missing: {ex.FileName}" }).ToList()
        });
    }
    catch (Exception ex)
    {
        endpointLogger.LogError(ex, "Error fetching files from Azure Blob Storage.");
        return Results.Ok(new BatchExecuteResponse
        {
            CompilationSuccess = false,
            CompilerOutput = "File error: Could not fetch required files from storage. Debug Reason: " + ex.Message,
            TestCaseResults = request.TestCases.Select(tc => new TestCaseResult { TestCaseId = tc.TestCaseId, Status = EvaluationStatus.FileError, Message = "Failed to fetch files." }).ToList()
        });
    }

    // 3. Call Evaluation Logic Service with Content
    try
    {
        endpointLogger.LogInformation("Calling batch evaluation logic for language {Language}", request.Language);
        var result = await evaluationLogic.EvaluateBatchAsync(
            codeContent,
            testCasesData,
            workingDirectory
        );
        endpointLogger.LogInformation("Batch evaluation logic completed. Compilation: {CompileStatus}", result.CompilationSuccess);
        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        endpointLogger.LogError(ex, "Unhandled exception during batch evaluation logic for CodeFile: {CodeFilePath}", request.CodeFilePath);
        return Results.Ok(new BatchExecuteResponse
        {
            CompilationSuccess = false,
            CompilerOutput = "Internal error during batch evaluation logic execution. Debug: " + ex.Message,
            TestCaseResults = request.TestCases.Select(tc => new TestCaseResult { TestCaseId = tc.TestCaseId, Status = EvaluationStatus.InternalError, Message = "Unexpected error in runner." }).ToList()
        });
    }
})
.WithName("ExecuteBatchCode").WithTags("Execution").RequireAuthorization("ApiKeyPolicy");

app.Run();

// --- Helper Function for Blob Fetching (can live in Program.cs or a helper class) ---
async Task<string> FetchBlobContentAsync(BlobContainerClient containerClient, string blobPath, ILogger logger)
{
    if (string.IsNullOrWhiteSpace(blobPath)) throw new ArgumentNullException(nameof(blobPath));
    logger.LogDebug("Fetching blob content from: {BlobPath}", blobPath);
    var blobClient = containerClient.GetBlobClient(blobPath);

    try
    {
        BlobDownloadStreamingResult downloadResult = await blobClient.DownloadStreamingAsync();
        using (var reader = new StreamReader(downloadResult.Content, Encoding.UTF8))
        {
            return await reader.ReadToEndAsync();
        }
    }
    catch (Azure.RequestFailedException rfEx) when (rfEx.Status == 404)
    {
        logger.LogError("Blob not found at path: {BlobPath}", blobPath);
        throw new FileNotFoundException($"Blob not found: {blobPath}", blobPath, rfEx); // Throw specific exception
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to download blob: {BlobPath}", blobPath);
        throw; // Re-throw other exceptions
    }
}