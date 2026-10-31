using System.IdentityModel.Tokens.Jwt;
using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Services;
using Xunit;

namespace Gentle.Book.API.Tests;

// Found during the UX/legal-prep audit: an impersonation-issued TenantAdmin token had the exact
// same lifetime as a normal login (Jwt:ExpiryHours, 8h by default) — a SuperAdmin support session
// could stay valid far longer than the support task itself, which the tenant AVV expects to be
// "only as long as needed". GenerateTenantAdminToken now forces a much shorter expiry whenever
// impersonatedBy is set, regardless of the configured normal login expiry.
public class ImpersonationSessionLifetimeTests
{
    [Fact]
    public void GenerateTenantAdminToken_NormalLogin_UsesConfiguredExpiry()
    {
        var jwt = new JwtService(TestConfiguration.Build());

        var token = jwt.GenerateTenantAdminToken(Guid.NewGuid(), "owner@salon.de", Guid.NewGuid(), "salon");

        var jwtToken = new JwtSecurityTokenHandler().ReadJwtToken(token);
        var lifetime = jwtToken.ValidTo - DateTime.UtcNow;
        Assert.True(lifetime > TimeSpan.FromHours(7), $"expected ~8h lifetime, got {lifetime}");
    }

    [Fact]
    public void GenerateTenantAdminToken_Impersonated_UsesShortSessionLifetime()
    {
        var jwt = new JwtService(TestConfiguration.Build());

        var token = jwt.GenerateTenantAdminToken(Guid.NewGuid(), "owner@salon.de", Guid.NewGuid(), "salon",
            impersonatedBy: "berkcan@gentlegroup.de");

        var jwtToken = new JwtSecurityTokenHandler().ReadJwtToken(token);
        var lifetime = jwtToken.ValidTo - DateTime.UtcNow;
        Assert.True(lifetime <= JwtService.ImpersonationSessionLifetime + TimeSpan.FromMinutes(1),
            $"expected ~{JwtService.ImpersonationSessionLifetime} lifetime, got {lifetime}");
        Assert.True(lifetime > TimeSpan.FromMinutes(55), $"expected ~{JwtService.ImpersonationSessionLifetime} lifetime, got {lifetime}");
    }
}
