using Azure.Storage.Blobs;
using GenericRunnerApi.Services;
using WebCodeWorkExecutor.Dtos;
using WebCodeWorkExecutor.Services;
using Azure.Storage.Blobs.Models;
using System.Text;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Headers;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.json",
        optional: false,
        reloadOnChange: true);

var configuredLanguage = builder.Configuration.GetValue<string>("Execution:Language")?.ToLowerInvariant()!;
var workingDirectory = builder.Configuration.GetValue<string>("Execution:WorkingDirectory")!;

if (string.IsNullOrEmpty(configuredLanguage))
{
    throw new InvalidOperationException("Execution:Language configuration is required.");
}

if (!Directory.Exists(workingDirectory)) Directory.CreateDirectory(workingDirectory);

builder.Services.AddProblemDetails();
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = "OrchestratorApiKey";
    options.DefaultChallengeScheme = "OrchestratorApiKey";
    options.DefaultScheme = "OrchestratorApiKey";
})
.AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>("OrchestratorApiKey", options =>
{
    options.ApiKeyHeaderName = builder.Configuration.GetValue<string>("Orchestrator:ApiHeaderName")!;
    options.ValidApiKey = builder.Configuration.GetValue<string>("Orchestrator:ApiKey")!;
});
builder.Services.AddAuthorization();

builder.Services.AddEndpointsApiExplorer();

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
    case "java":
        builder.Services.AddScoped<ILanguageSpecificLogic, JavaSpecificLogicProvider>();
        break;
    case "rust":
        builder.Services.AddScoped<ILanguageSpecificLogic, RustSpecificLogicProvider>();
        break;
    case "go":
        builder.Services.AddScoped<ILanguageSpecificLogic, GoSpecificLogicProvider>();
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
        var cwd = System.Environment.CurrentDirectory;

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

builder.Services.AddHealthChecks();
builder.Services.AddHttpClient("OrchestratorClient", client =>
{
    client.BaseAddress = new Uri($"{builder.Configuration.GetValue<string>("Orchestrator:Address")!}");
    client.DefaultRequestHeaders.Add(builder.Configuration.GetValue<string>("Orchestrator:ApiHeaderName")!, builder.Configuration.GetValue<string>("Orchestrator:ApiKey"));
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
});
builder.Services.AddScoped<IOrchestratorService, OrchestratorService>();

var app = builder.Build();

app.UseExceptionHandler(exceptionHandlerApp =>
{
    exceptionHandlerApp.Run(async context =>
    {
        var exceptionHandlerPathFeature = context.Features.Get<IExceptionHandlerPathFeature>();
        var exception = exceptionHandlerPathFeature?.Error;
        var unhandledLogger = context.RequestServices.GetRequiredService<ILogger<Program>>(); 

        unhandledLogger.LogError(exception, "Global unhandled exception: {ErrorMessage}", exception?.Message);

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/problem+json";

        var problemDetails = new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = $"Unhandled Exception in Runner: {exception?.GetType().Name}",
            Instance = context.Request.Path,
            Detail = exception?.ToString(), 
        };
        await context.Response.WriteAsJsonAsync(problemDetails);
    });
});

app.UseAuthentication();
app.UseAuthorization();

app.MapPost("/execute", async (
    BatchExecuteRequest request,
    ICodeEvaluationLogic evaluationLogic,
    BlobServiceClient blobServiceClient,
    IConfiguration configuration,
    ILogger<Program> endpointLogger,
    IOrchestratorService orchestratorSvc
    ) =>
{
    endpointLogger.LogInformation("Batch execute request received for lang: {Lang}, code: {CodeFile}, TCs: {TCCount}",
    request.Language, request.CodeFilePath, request.TestCases.Count);

    var containerName = configuration.GetValue<string>("AzureStorage:ContainerName");
    if (string.IsNullOrEmpty(containerName))
    {
        endpointLogger.LogCritical("Azure Storage container name not configured.");
        return Results.Problem();
    }
    var containerClient = blobServiceClient.GetBlobContainerClient(containerName);
    _ = StartEvaluation(request, endpointLogger, containerClient, evaluationLogic, orchestratorSvc);
    return Results.Ok();
})
.WithName("ExecuteBatchCode").WithTags("Execution").RequireAuthorization();

app.MapHealthChecks("/health");
app.Run();


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
        throw new FileNotFoundException($"Blob not found: {blobPath}", blobPath, rfEx);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to download blob: {BlobPath}", blobPath);
        throw;
    }
}

BatchExecuteResponse GetExceptionResponse(string message, int submissionId)
{
    return new BatchExecuteResponse
    {
        CompilationSuccess = false,
        CompilerOutput = message,
        TestCaseResults = [],
        SubmissionId = submissionId,
    };
}

async Task StartEvaluation(BatchExecuteRequest request, ILogger<Program> endpointLogger, BlobContainerClient containerClient, ICodeEvaluationLogic evaluationLogic, IOrchestratorService orchestratorSvc)
{
    string codeContent;
    List<TestCaseEvaluationData> testCasesData = new List<TestCaseEvaluationData>();

    try
    {
        endpointLogger.LogDebug("Fetching code file from: {CodeFilePath}", request.CodeFilePath);
        codeContent = await FetchBlobContentAsync(containerClient, request.CodeFilePath, endpointLogger);
        endpointLogger.LogDebug("Code file fetched successfully.");

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
        _ = orchestratorSvc.SendEvaluation(GetExceptionResponse("A required file was not found in blob storage.", request.SubmissionId));
        return;
    }
    catch (Exception ex)
    {
        endpointLogger.LogError(ex, "Error fetching files from Azure Blob Storage.");
        _ = orchestratorSvc.SendEvaluation(GetExceptionResponse("Error fetching files from Azure Blob Storage.", request.SubmissionId));
        return;
    }

    try
    {
        endpointLogger.LogInformation("Calling batch evaluation logic for language {Language}", request.Language);
        var result = await evaluationLogic.EvaluateBatchAsync(
            codeContent,
            testCasesData,
            workingDirectory,
            request.SubmissionId
        );
        endpointLogger.LogInformation("Batch evaluation logic completed. Compilation: {CompileStatus}", result.CompilationSuccess);
        _ = orchestratorSvc.SendEvaluation(result);
        return;
    }
    catch (Exception ex)
    {
        endpointLogger.LogError(ex, "Unhandled exception during batch evaluation logic for CodeFile: {CodeFilePath}", request.CodeFilePath);
        _ = orchestratorSvc.SendEvaluation(GetExceptionResponse("Internal error", request.SubmissionId));
        return;
    }
}