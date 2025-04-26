// Program.cs
using Microsoft.AspNetCore.Authentication;
using WebCodeWorkExecutor.Authentication; // Your auth namespace
using Microsoft.OpenApi.Models;
using Azure.Storage.Blobs;
using GenericRunnerApi.Services;
using GenericRunnerApi.Dtos;

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
        builder.Services.AddScoped<ICodeExecutionLogic, CExecutionLogic>();
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

app.MapPost("/compile", async (CompileRequest request, ICodeExecutionLogic executionLogic, ILogger<Program> logger) => // Inject Interface
{
    logger.LogInformation("Compile request received for: {CodeFile}", request.CodeFileName);
    try
    {
        var result = await executionLogic.CompileAsync(request, workingDirectory); // Call service
        return Results.Ok(result);
    }
    catch (Exception ex)
    {
         logger.LogError(ex, "Unhandled exception during /compile for {CodeFile}", request.CodeFileName);
         return Results.Problem("An unexpected error occurred during compilation.", statusCode: 500);
    }
})
.WithName("CompileCode").WithTags("Execution").RequireAuthorization("ApiKeyPolicy");

app.MapPost("/run", async (RunRequest request, ICodeExecutionLogic executionLogic, ILogger<Program> logger) => // Inject Interface
{
     logger.LogInformation("Run request received for: {ExePath} with Input: {InputFile}", request.ExecutablePath, request.InputFileName ?? "None");
    try
    {
         var result = await executionLogic.RunAsync(request, workingDirectory); // Call service
         return Results.Ok(result);
    }
     catch (Exception ex)
    {
         logger.LogError(ex, "Unhandled exception during /run for {ExePath}", request.ExecutablePath);
         return Results.Problem("An unexpected error occurred during execution.", statusCode: 500);
    }
})
.WithName("RunCode").WithTags("Execution").RequireAuthorization("ApiKeyPolicy");




app.Run();