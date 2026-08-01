using Microsoft.Extensions.Configuration;

namespace Gentle.Book.API.Tests.TestSupport;

/// <summary>
/// A minimal, self-contained IConfiguration with everything JwtService/EmailService need —
/// deliberately independent of any appsettings.*.json file (those are gitignored and won't
/// exist in a fresh clone or CI runner), so tests never depend on local machine state.
/// </summary>
public static class TestConfiguration
{
    public static IConfiguration Build() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Secret"] = "test-signing-secret-at-least-32-bytes-long-for-hmac-sha256",
            ["Jwt:Issuer"] = "gentlebook-tests",
            ["Jwt:Audience"] = "gentlebook-tests-client",
            ["Jwt:ExpiryHours"] = "8",
            ["Jwt:SuperAdminSecret"] = "test-superadmin-signing-secret-at-least-32-bytes-long",
            ["Jwt:SuperAdminIssuer"] = "gentlebook-tests-superadmin",
            ["Jwt:SuperAdminAudience"] = "gentlebook-tests-superadmin-client",
        })
        .Build();
}
