// GentleBook.Api.Services/JwtService.cs
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace GentleBook.Api.Services;

public class JwtService
{
    private readonly IConfiguration _config;

    public JwtService(IConfiguration config)
    {
        _config = config;
    }

    // ── Employee token (used for employees logging into tenant admin) ───────
    public string GenerateEmployeeToken(Guid employeeId, string name, string username, string role,
        Guid tenantId, string tenantSlug)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, employeeId.ToString()),
            new(JwtRegisteredClaimNames.Name, name),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new("username", username),
            new("role", role),
            new("tenantId", tenantId.ToString()),
            new("tenantSlug", tenantSlug),
        };
        return BuildToken(claims, "Jwt:Secret", "Jwt:Issuer", "Jwt:Audience", "Jwt:ExpiryHours");
    }

    // ── TenantAdmin token ───────────────────────────────────────────────────
    // impersonatedBy: set when a SuperAdmin generates this token via impersonation —
    // makes impersonated sessions distinguishable in logs and audits.
    // role/locationId: also reused for LocationAdmin (Agency-exclusive, scoped to one
    // BusinessLocation) — same token shape, just a different role claim plus locationId.
    public string GenerateTenantAdminToken(Guid userId, string email, Guid tenantId, string tenantSlug,
        string? impersonatedBy = null, string role = "TenantAdmin", Guid? locationId = null)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new(JwtRegisteredClaimNames.Email, email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new("role", role),
            new("tenantId", tenantId.ToString()),
            new("tenantSlug", tenantSlug),
        };
        if (!string.IsNullOrEmpty(impersonatedBy))
            claims.Add(new Claim("impersonatedBy", impersonatedBy));
        if (locationId.HasValue)
            claims.Add(new Claim("locationId", locationId.Value.ToString()));
        return BuildToken(claims, "Jwt:Secret", "Jwt:Issuer", "Jwt:Audience", "Jwt:ExpiryHours");
    }

    public static string? GetImpersonatedBy(ClaimsPrincipal user)
        => user.FindFirstValue("impersonatedBy");

    // ── SuperAdmin token ────────────────────────────────────────────────────
    public string GenerateSuperAdminToken(Guid userId, string email)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new(JwtRegisteredClaimNames.Email, email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new("role", "SuperAdmin"),
        };
        return BuildToken(claims, "Jwt:SuperAdminSecret", "Jwt:SuperAdminIssuer", "Jwt:SuperAdminAudience", "Jwt:ExpiryHours");
    }

    // ── Backward-compatible wrapper (still used by EmployeeAuthController) ──
    public string GenerateToken(Guid employeeId, string name, string username, string role)
        => GenerateEmployeeToken(employeeId, name, username, role, Guid.Empty, string.Empty);

    // ── Private builder ─────────────────────────────────────────────────────
    private string BuildToken(List<Claim> claims, string secretKey, string issuerKey, string audienceKey, string expiryKey)
    {
        var secret = _config[secretKey]
            ?? throw new InvalidOperationException($"{secretKey} is not configured");
        var issuer = _config[issuerKey]
            ?? throw new InvalidOperationException($"{issuerKey} is not configured");
        var audience = _config[audienceKey]
            ?? throw new InvalidOperationException($"{audienceKey} is not configured");

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expiryHours = int.TryParse(_config[expiryKey], out var h) ? h : 8;

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.AddHours(expiryHours),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    // ── Claim extractors ────────────────────────────────────────────────────
    public static Guid? GetUserId(ClaimsPrincipal user)
    {
        var sub = user.FindFirstValue(JwtRegisteredClaimNames.Sub)
               ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out var id) ? id : null;
    }

    public static Guid? GetEmployeeId(ClaimsPrincipal user) => GetUserId(user);

    public static Guid? GetTenantId(ClaimsPrincipal user)
    {
        var claim = user.FindFirstValue("tenantId");
        return Guid.TryParse(claim, out var id) ? id : null;
    }

    public static string? GetTenantSlug(ClaimsPrincipal user)
        => user.FindFirstValue("tenantSlug");

    public static Guid? GetLocationId(ClaimsPrincipal user)
    {
        var claim = user.FindFirstValue("locationId");
        return Guid.TryParse(claim, out var id) ? id : null;
    }

    public static string? GetRole(ClaimsPrincipal user)
        => user.FindFirstValue("role");

    public static bool IsSuperAdmin(ClaimsPrincipal user)
        => GetRole(user) == "SuperAdmin";

    public static string? GetUsername(ClaimsPrincipal user)
        => user.FindFirstValue("username");

    public static string? GetName(ClaimsPrincipal user)
        => user.FindFirstValue(JwtRegisteredClaimNames.Name)
        ?? user.FindFirstValue(ClaimTypes.Name);
}
