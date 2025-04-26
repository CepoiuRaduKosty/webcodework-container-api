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

var builder = WebApplication.CreateBuilder(args);
var logger = builder.Logging.Services.BuildServiceProvider().GetRequiredService<ILogger<Program>>(); // Get logger early for config phase

// --- Configuration ---
// Language is read from config/env vars later
var configuredLanguage = builder.Configuration.GetValue<string>("Execution:Language")?.ToLowerInvariant() ?? "c";
var workingDirectory = builder.Configuration.GetValue<string>("Execution:WorkingDirectory") ?? "/sandbox";

if (string.IsNullOrEmpty(configuredLanguage))
{
    logger.LogCritical("Execution:Language configuration is missing!");
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
builder.Services.AddAuthorization(options => {
     options.AddPolicy("ApiKeyPolicy", policy => {
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
    options.AddSecurityDefinition(ApiKeyAuthenticationDefaults.AuthenticationScheme, new OpenApiSecurityScheme { /* ... Same as orchestrator ... */ });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement { /* ... Same as orchestrator ... */ });
});

// --- NEW: Register Language-Specific Execution Logic ---
switch (configuredLanguage)
{
    case "c":
        // Register the concrete implementation for the interface
        builder.Services.AddScoped<ICodeEvaluationLogic, CEvaluationLogic>();
        logger.LogInformation("Registered CExecutionLogic for ICodeExecutionLogic.");
        break;
    // case "python":
    //    builder.Services.AddScoped<ICodeExecutionLogic, PythonExecutionLogic>(); // When you create it
    //    logger.LogInformation("Registered PythonExecutionLogic for ICodeExecutionLogic.");
    //    break;
    default:
        var errorMessage = $"Unsupported language configured: '{configuredLanguage}'";
        logger.LogCritical(errorMessage);
        throw new NotSupportedException(errorMessage); // Fail fast on unsupported config
}

builder.Services.AddSingleton(sp => {
    var configuration = sp.GetRequiredService<IConfiguration>();
    var connectionString = configuration.GetValue<string>("AzureStorage:ConnectionString");
     if (string.IsNullOrEmpty(connectionString)) throw new InvalidOperationException("Azure Storage Connection String not configured.");
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
app.UseExceptionHandler();
// No HTTPS redirection needed for internal API called directly via IP/localhost/container name
// app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();


// --- API Endpoints ---

// Basic health check
app.MapGet("/", () => $"Generic Runner API ({configuredLanguage}) - Running")
    .ExcludeFromDescription(); // Hide from Swagger

app.MapPost("/execute", async (
    ExecuteRequest request,              // Request DTO with paths
    ICodeEvaluationLogic evaluationLogic, // Injected language logic service
    BlobServiceClient blobServiceClient,   // Inject Blob client HERE
    IConfiguration configuration,         // Inject Configuration HERE
    ILogger<Program> endpointLogger       // Inject Logger HERE (or specific controller logger)
    ) =>
{
     endpointLogger.LogInformation("Execute request received for language: {Language}, CodeFile: {CodeFilePath}", request.Language, request.CodeFilePath);

     string codeContent, inputContent, expectedOutputContent;
     var containerName = configuration.GetValue<string>("AzureStorage:ContainerName");
     if (string.IsNullOrEmpty(containerName))
     {
          endpointLogger.LogCritical("Azure Storage container name not configured.");
          return Results.Ok(new ExecuteResponse { Status = EvaluationStatus.InternalError, Message = "Storage container not configured." });
          // Or Results.Problem(...) for 500
     }
     var containerClient = blobServiceClient.GetBlobContainerClient(containerName);

     // 1. Fetch Files (moved from service to endpoint handler)
     try
     {
         endpointLogger.LogDebug("Fetching files from blob storage...");
         var fetchTasks = new[] {
             FetchBlobContentAsync(containerClient, request.CodeFilePath, endpointLogger),
             FetchBlobContentAsync(containerClient, request.InputFilePath, endpointLogger),
             FetchBlobContentAsync(containerClient, request.ExpectedOutputFilePath, endpointLogger)
         };
         await Task.WhenAll(fetchTasks);
         codeContent = await fetchTasks[0];
         inputContent = await fetchTasks[1];
         expectedOutputContent = await fetchTasks[2];
          endpointLogger.LogDebug("Successfully fetched all required files.");
     }
     catch (FileNotFoundException ex) // Catch exception from helper
     {
         endpointLogger.LogError(ex, "Required file not found in blob storage.");
         return Results.Ok(new ExecuteResponse { Status = EvaluationStatus.FileError, Message = $"Required file not found: {ex.Message}" });
         // Or Results.BadRequest / Results.NotFound
     }
     catch (Exception ex)
     {
          endpointLogger.LogError(ex, "Error fetching files from Azure Blob Storage.");
          return Results.Ok(new ExecuteResponse { Status = EvaluationStatus.FileError, Message = "Failed to fetch required files from storage." });
          // Or Results.Problem(...) for 500
     }

     // 2. Call Evaluation Logic Service (now passing content)
     try
     {
         endpointLogger.LogInformation("Calling evaluation logic for language {Language}", request.Language);
         // Call the service with CONTENT, not the request DTO
         var result = await evaluationLogic.EvaluateAsync(
             codeContent,
             inputContent,
             expectedOutputContent,
             request.TimeLimitSeconds,
             workingDirectory // Pass the configured working directory
             );
         endpointLogger.LogInformation("Evaluation logic completed with status: {Status}", result.Status);
         return Results.Ok(result); // Return the result from the service
     }
     catch (Exception ex)
     {
         endpointLogger.LogError(ex, "Unhandled exception during evaluation logic execution for CodeFile: {CodeFilePath}", request.CodeFilePath);
         return Results.Ok(new ExecuteResponse{ Status = EvaluationStatus.InternalError, Message = "An unexpected internal error occurred during evaluation logic execution." });
         // Or Results.Problem(...) for 500
     }
})
.WithName("ExecuteCode").WithTags("Execution").RequireAuthorization("ApiKeyPolicy");

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