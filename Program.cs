// Program.cs
using Microsoft.AspNetCore.Authentication;
using WebCodeWorkExecutor.Authentication; // Your auth namespace
using Microsoft.OpenApi.Models;
using WebCodeWorkContainer.Dtos; // Your DTO namespace
using System.Diagnostics; // For Process
using System.Text; // For Encoding

var builder = WebApplication.CreateBuilder(args);

// --- Configuration ---
// Language is read from config/env vars later
var configuredLanguage = builder.Configuration.GetValue<string>("Execution:Language")?.ToLowerInvariant() ?? "c";
var workingDirectory = builder.Configuration.GetValue<string>("Execution:WorkingDirectory") ?? "/sandbox";

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

// --- Application ---
var app = builder.Build();

// --- Middleware ---
if (app.Environment.IsDevelopment())
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


app.Run();