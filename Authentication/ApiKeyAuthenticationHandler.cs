
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using System.Linq; 

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
            
            if (!Request.Headers.TryGetValue(ApiKeyAuthenticationDefaults.ApiKeyHeaderName, out var apiKeyHeaderValues))
            {
                _logger.LogDebug("'{HeaderName}' header not found.", ApiKeyAuthenticationDefaults.ApiKeyHeaderName);
                return Task.FromResult(AuthenticateResult.NoResult()); 
            }

            var providedApiKey = apiKeyHeaderValues.FirstOrDefault();
            if (apiKeyHeaderValues.Count == 0 || string.IsNullOrWhiteSpace(providedApiKey))
            {
                 _logger.LogWarning("'{HeaderName}' header found but value is missing.", ApiKeyAuthenticationDefaults.ApiKeyHeaderName);
                return Task.FromResult(AuthenticateResult.Fail($"Header '{ApiKeyAuthenticationDefaults.ApiKeyHeaderName}' is missing or empty."));
            }

            
            var expectedApiKey = _configuration.GetValue<string>("Authentication:ApiKey");

            if (string.IsNullOrWhiteSpace(expectedApiKey))
            {
                
                _logger.LogCritical("API Key ('Authentication:ApiKey') not configured on the server.");
                return Task.FromResult(AuthenticateResult.Fail("Server configuration error: API Key is missing."));
            }

            
            if (!string.Equals(providedApiKey, expectedApiKey, StringComparison.Ordinal)) 
            {
                 _logger.LogWarning($"Invalid API Key provided. Configured [{expectedApiKey}] ; Provided [{providedApiKey}]");
                 return Task.FromResult(AuthenticateResult.Fail("Invalid API Key provided."));
            }

            
            _logger.LogDebug("API Key validated successfully.");
            var claims = new[] {
                
                 new Claim(ClaimTypes.NameIdentifier, "BackendService"), 
                 new Claim(ClaimTypes.Name, "BackendService")
                 
                 
             };
            var identity = new ClaimsIdentity(claims, Scheme.Name); 
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);

            return Task.FromResult(AuthenticateResult.Success(ticket));
        }

        
        
        
    }
}