using System.Security.Claims;

namespace Gentle.Book.API.Tests.TestSupport;

/// <summary>
/// Builds the same claim shape JwtService puts on a real token, so controller tests exercise
/// the exact claim-reading code (JwtService.GetRole/GetTenantId/GetUserId) the live app uses —
/// without needing to encode/decode a real signed JWT for every test.
/// </summary>
public static class ClaimsPrincipalFactory
{
    public static ClaimsPrincipal TenantAdmin(Guid userId, Guid tenantId, string tenantSlug = "test-tenant") =>
        Build(userId, "TenantAdmin", tenantId, tenantSlug);

    public static ClaimsPrincipal Employee(Guid employeeId, Guid tenantId, string tenantSlug = "test-tenant") =>
        Build(employeeId, "Employee", tenantId, tenantSlug);

    public static ClaimsPrincipal SuperAdmin(Guid userId) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim("sub", userId.ToString()),
            new Claim("role", "SuperAdmin"),
        }, "TestAuth"));

    private static ClaimsPrincipal Build(Guid userId, string role, Guid tenantId, string tenantSlug) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim("sub", userId.ToString()),
            new Claim("role", role),
            new Claim("tenantId", tenantId.ToString()),
            new Claim("tenantSlug", tenantSlug),
        }, "TestAuth"));
}
