using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

public class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    public const string SchemeName = "ApiKey";
    public string ApiKeyHeaderName { get; set; } = "";
    public string ValidApiKey { get; set; } = "";
}

public class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    public ApiKeyAuthenticationHandler(IOptionsMonitor<ApiKeyAuthenticationOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        Logger.LogInformation($"Request to auth debug: {Request.Headers}");
        if (!Request.Headers.ContainsKey(Options.ApiKeyHeaderName))
        {
            return AuthenticateResult.Fail($"Missing '{Options.ApiKeyHeaderName}' header.");
        }
        string? apiKey = Request.Headers[Options.ApiKeyHeaderName].FirstOrDefault();
        if (apiKey != Options.ValidApiKey)
        {
            return AuthenticateResult.Fail("Invalid API Key.");
        }

        var claims = new[] {
            new Claim("scope", "api_access_orchestrator")
        };
        var identity = new ClaimsIdentity(claims, ApiKeyAuthenticationOptions.SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, ApiKeyAuthenticationOptions.SchemeName);

        return AuthenticateResult.Success(ticket);
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        Response.Headers.Append("WWW-Authenticate", $"{Scheme.Name} realm=\"{Request.Host}\"");
        await Response.CompleteAsync();
    }

    protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 403;
        await Response.CompleteAsync();
    }
}