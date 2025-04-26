// Authentication/ApiKeyAuthenticationHandler.cs
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using System.Linq; // Required for Linq methods like FirstOrDefault

namespace WebCodeWorkExecutor.Authentication
{
    public class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<ApiKeyAuthenticationHandler> _logger;

        public ApiKeyAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory loggerFactory,
            UrlEncoder encoder,
            ISystemClock clock,
            IConfiguration configuration)
            : base(options, loggerFactory, encoder, clock)
        {
            _configuration = configuration;
            _logger = loggerFactory.CreateLogger<ApiKeyAuthenticationHandler>();
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            // 1. Check if the API Key header exists
            if (!Request.Headers.TryGetValue(ApiKeyAuthenticationDefaults.ApiKeyHeaderName, out var apiKeyHeaderValues))
            {
                _logger.LogDebug("'{HeaderName}' header not found.", ApiKeyAuthenticationDefaults.ApiKeyHeaderName);
                return Task.FromResult(AuthenticateResult.NoResult()); // Header not found - pass to next handler or fail if default
            }

            var providedApiKey = apiKeyHeaderValues.FirstOrDefault();
            if (apiKeyHeaderValues.Count == 0 || string.IsNullOrWhiteSpace(providedApiKey))
            {
                 _logger.LogWarning("'{HeaderName}' header found but value is missing.", ApiKeyAuthenticationDefaults.ApiKeyHeaderName);
                return Task.FromResult(AuthenticateResult.Fail($"Header '{ApiKeyAuthenticationDefaults.ApiKeyHeaderName}' is missing or empty."));
            }

            // 2. Get the expected API Key from configuration
            var expectedApiKey = _configuration.GetValue<string>("Authentication:ApiKey");

            if (string.IsNullOrWhiteSpace(expectedApiKey))
            {
                // This is a server configuration error
                _logger.LogCritical("API Key ('Authentication:ApiKey') not configured on the server.");
                return Task.FromResult(AuthenticateResult.Fail("Server configuration error: API Key is missing."));
            }

            // 3. Compare the keys (Constant-time comparison is slightly better against timing attacks, but less critical for internal API keys)
            if (!string.Equals(providedApiKey, expectedApiKey, StringComparison.Ordinal)) // Use Ordinal for keys
            {
                 _logger.LogWarning("Invalid API Key provided.");
                 return Task.FromResult(AuthenticateResult.Fail("Invalid API Key provided."));
            }

            // 4. Create authenticated principal if key is valid
            _logger.LogDebug("API Key validated successfully.");
            var claims = new[] {
                // Add claims identifying the caller if needed, e.g., differentiating internal services
                 new Claim(ClaimTypes.NameIdentifier, "BackendService"), // Example claim
                 new Claim(ClaimTypes.Name, "BackendService")
                 // Could add roles if you have different API keys with different permissions
                 // new Claim(ClaimTypes.Role, "CodeRunner")
             };
            var identity = new ClaimsIdentity(claims, Scheme.Name); // Use Scheme.Name (which is "ApiKey")
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);

            return Task.FromResult(AuthenticateResult.Success(ticket));
        }

        // Optional: Override HandleChallengeAsync or HandleForbiddenAsync for custom 401/403 responses
        // protected override Task HandleChallengeAsync(AuthenticationProperties properties) { ... }
        // protected override Task HandleForbiddenAsync(AuthenticationProperties properties) { ... }
    }
}