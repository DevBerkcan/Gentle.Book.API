// Controllers/SuperAdminAuthController.cs
// Login endpoint exclusively for SuperAdmin.
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Controllers;

[ApiController]
[Route("api/auth/superadmin")]
public class SuperAdminAuthController : ControllerBase
{
    private readonly GentleBookDbContext _db;
    private readonly JwtService _jwt;

    public SuperAdminAuthController(GentleBookDbContext db, JwtService jwt)
    {
        _db = db;
        _jwt = jwt;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] SuperAdminLoginDto dto)
    {
        var user = await _db.PlatformUsers
            .FirstOrDefaultAsync(u => u.TenantId == null
                                   && u.Role == PlatformRole.SuperAdmin
                                   && u.Email == dto.Email.ToLowerInvariant()
                                   && u.IsActive);

        if (user == null || !BCrypt.Net.BCrypt.Verify(dto.Password, user.PasswordHash))
            return Unauthorized(new { message = "Invalid credentials." });

        user.LastLoginAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var token = _jwt.GenerateSuperAdminToken(user.Id, user.Email);

        return Ok(new
        {
            token,
            user = new
            {
                id = user.Id,
                email = user.Email,
                firstName = user.FirstName,
                lastName = user.LastName,
                role = "SuperAdmin",
            }
        });
    }

    /// <summary>
    /// One-time bootstrap: creates the first SuperAdmin account.
    /// Requires the AdminBootstrapSecret header. Disabled after first use.
    /// </summary>
    [HttpPost("bootstrap")]
    [AllowAnonymous]
    public async Task<IActionResult> Bootstrap([FromBody] BootstrapDto dto, [FromServices] IConfiguration config)
    {
        var secret = config["AdminBootstrapSecret"]
            ?? throw new InvalidOperationException("AdminBootstrapSecret is not configured");

        if (!Request.Headers.TryGetValue("X-Bootstrap-Secret", out var val) || val != secret)
            return Unauthorized(new { message = "Invalid bootstrap secret." });

        var alreadyExists = await _db.PlatformUsers.AnyAsync(u => u.Role == PlatformRole.SuperAdmin);
        if (alreadyExists)
            return Conflict(new { message = "SuperAdmin already exists. Bootstrap is one-time only." });

        var admin = new PlatformUser
        {
            Email = dto.Email.ToLowerInvariant(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.Password, workFactor: 12),
            FirstName = dto.FirstName,
            LastName = dto.LastName,
            Role = PlatformRole.SuperAdmin,
            TenantId = null,
        };

        _db.PlatformUsers.Add(admin);
        await _db.SaveChangesAsync();

        return Ok(new { message = "SuperAdmin created successfully.", id = admin.Id });
    }
}

public record SuperAdminLoginDto(string Email, string Password);
public record BootstrapDto(string Email, string Password, string FirstName, string LastName);
