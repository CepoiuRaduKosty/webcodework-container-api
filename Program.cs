
using Microsoft.AspNetCore.Authentication;
using WebCodeWorkExecutor.Authentication; 
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

var configuredLanguage = builder.Configuration.GetValue<string>("Execution:Language")?.ToLowerInvariant() ?? "c";
var workingDirectory = builder.Configuration.GetValue<string>("Execution:WorkingDirectory") ?? "/sandbox";

if (string.IsNullOrEmpty(configuredLanguage))
{
    throw new InvalidOperationException("Execution:Language configuration is required.");
}

if (!Directory.Exists(workingDirectory)) Directory.CreateDirectory(workingDirectory);

builder.Services.AddProblemDetails();

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

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = $"Runner API ({configuredLanguage})", Version = "v1" });
    
    options.AddSecurityDefinition(ApiKeyAuthenticationDefaults.AuthenticationScheme, new OpenApiSecurityScheme
    {
        In = ParameterLocation.Header, 
        Description = "Please enter the API Key", 
        Name = "X-Api-Key", 
        Type = SecuritySchemeType.ApiKey, 
        Scheme = "ApiKeyScheme" 
    });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement {
    {
        new OpenApiSecurityScheme
        {
            Reference = new OpenApiReference
            {
                Type = ReferenceType.SecurityScheme,
                Id = ApiKeyAuthenticationDefaults.AuthenticationScheme 
            }
        },
        new string[] {} 
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
app.MapGet("/", () => $"Generic Runner API ({configuredLanguage}) - Running")
    .ExcludeFromDescription(); 

app.MapPost("/execute", async (
    BatchExecuteRequest request,        
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
        { 
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