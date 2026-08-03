using System.Security.Claims;
using System.Text.Encodings.Web;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace GentleBook.Api.Middleware;

public class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";
}

// Second parallel auth scheme alongside the default JWT Bearer scheme (Program.cs). Only
// applies to controllers explicitly marked [Authorize(AuthenticationSchemes = "ApiKey")] —
// a plain [Authorize] elsewhere keeps using the default JWT scheme only, so an API key can
// never accidentally authenticate against the rest of the admin surface.
//
// Builds a ClaimsPrincipal with the same "role"/"tenantId" claim shape a JWT would carry, so
// it flows through the existing TenantMiddleware unmodified and populates ITenantContext the
// same way — no separate tenant-resolution path needed.
public class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    private readonly ApiKeyService _apiKeyService;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ApiKeyService apiKeyService)
        : base(options, logger, encoder)
    {
        _apiKeyService = apiKeyService;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyAuthenticationOptions.HeaderName, out var apiKeyHeader))
            return AuthenticateResult.Fail("Missing X-Api-Key header.");

        var tenantId = await _apiKeyService.ValidateAsync(apiKeyHeader.ToString());
        if (tenantId == null)
            return AuthenticateResult.Fail("Invalid or revoked API key.");

        var claims = new[]
        {
            new Claim("role", "TenantAdmin"),
            new Claim("tenantId", tenantId.Value.ToString()),
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }
}
