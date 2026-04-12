// Controllers/AuthController.cs
// Login for TenantAdmin users (created by SuperAdmin).
using System.Security.Cryptography;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.DTOs;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly GentleBookDbContext _db;
    private readonly JwtService _jwt;
    private readonly EmailService _email;
    private readonly ILogger<AuthController> _logger;

    public AuthController(GentleBookDbContext db, JwtService jwt, EmailService email, ILogger<AuthController> logger)
    {
        _db = db;
        _jwt = jwt;
        _email = email;
        _logger = logger;
    }

    /// <summary>
    /// Login for TenantAdmin — pass tenantSlug in body to identify the tenant.
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] TenantAdminLoginDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.TenantSlug))
            return BadRequest(new { message = "tenantSlug is required." });

        var tenant = await _db.Tenants
            .Include(t => t.Subscription)
            .FirstOrDefaultAsync(t => t.Slug == dto.TenantSlug.ToLowerInvariant() && t.IsActive);

        if (tenant == null)
            return Unauthorized(new { message = "Tenant not found or inactive." });

        if (tenant.Subscription == null || !tenant.Subscription.IsAccessAllowed)
            return StatusCode(402, new { message = "Trial expired or subscription inactive." });

        var user = await _db.PlatformUsers
            .FirstOrDefaultAsync(u => u.TenantId == tenant.Id
                                   && u.Email == dto.Email.ToLowerInvariant()
                                   && u.IsActive);

        if (user == null || !BCrypt.Net.BCrypt.Verify(dto.Password, user.PasswordHash))
            return Unauthorized(new { message = "Invalid credentials." });

        user.LastLoginAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var token = _jwt.GenerateTenantAdminToken(user.Id, user.Email, tenant.Id, tenant.Slug);

        return Ok(new
        {
            token,
            user = new
            {
                id = user.Id,
                email = user.Email,
                firstName = user.FirstName,
                lastName = user.LastName,
                role = user.Role.ToString(),
                tenantId = tenant.Id,
                tenantSlug = tenant.Slug,
                tenantName = tenant.Name,
            }
        });
    }

    /// <summary>Get current authenticated user info.</summary>
    [HttpGet("me")]
    [Authorize]
    public IActionResult Me()
    {
        return Ok(new
        {
            userId = JwtService.GetUserId(User),
            tenantId = JwtService.GetTenantId(User),
            tenantSlug = JwtService.GetTenantSlug(User),
            role = JwtService.GetRole(User),
        });
    }

    /// <summary>Change password for the currently authenticated TenantAdmin.</summary>
    [HttpPut("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordDto dto)
    {
        var userId = JwtService.GetUserId(User);
        if (userId == null) return Unauthorized();

        if (string.IsNullOrWhiteSpace(dto.CurrentPassword) || string.IsNullOrWhiteSpace(dto.NewPassword))
            return BadRequest(new { message = "Aktuelles und neues Passwort sind erforderlich." });

        if (dto.NewPassword.Length < 6)
            return BadRequest(new { message = "Das neue Passwort muss mindestens 6 Zeichen lang sein." });

        var user = await _db.PlatformUsers.FindAsync(userId.Value);
        if (user == null) return NotFound();

        if (!BCrypt.Net.BCrypt.Verify(dto.CurrentPassword, user.PasswordHash))
            return BadRequest(new { message = "Das aktuelle Passwort ist falsch." });

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.NewPassword, workFactor: 12);
        await _db.SaveChangesAsync();

        return Ok(new { message = "Passwort erfolgreich geändert." });
    }

    /// <summary>
    /// Request a password reset email. Always returns 200 to prevent user enumeration.
    /// </summary>
    [HttpPost("forgot-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.TenantSlug) || string.IsNullOrWhiteSpace(dto.Email))
            return Ok(new { message = "Wenn ein Konto gefunden wurde, wurde eine E-Mail gesendet." });

        var tenant = await _db.Tenants
            .FirstOrDefaultAsync(t => t.Slug == dto.TenantSlug.ToLowerInvariant() && t.IsActive);

        if (tenant != null)
        {
            var user = await _db.PlatformUsers
                .FirstOrDefaultAsync(u => u.TenantId == tenant.Id
                                       && u.Email == dto.Email.ToLowerInvariant()
                                       && u.IsActive);

            if (user != null)
            {
                // Invalidate old tokens
                var old = await _db.PasswordResetTokens
                    .Where(t => t.UserId == user.Id && !t.IsUsed)
                    .ToListAsync();
                foreach (var t in old) t.IsUsed = true;

                // Generate secure raw token + store hash
                var rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
                    .Replace("+", "-").Replace("/", "_").Replace("=", "");
                var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawToken)));

                _db.PasswordResetTokens.Add(new PasswordResetToken
                {
                    Id        = Guid.NewGuid(),
                    UserId    = user.Id,
                    TokenHash = hash,
                    ExpiresAt = DateTime.UtcNow.AddHours(1),
                    IsUsed    = false,
                    CreatedAt = DateTime.UtcNow,
                });
                await _db.SaveChangesAsync();

                var frontendBase = _email.FrontendUrl;
                var resetUrl = $"{frontendBase}/admin/reset-password?token={rawToken}";
                try
                {
                    await _email.SendPasswordResetEmailAsync(user.Email, user.FirstName, resetUrl);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send password reset email to {Email}", user.Email);
                }
            }
        }

        // Always return 200
        return Ok(new { message = "Wenn ein Konto gefunden wurde, wurde eine E-Mail gesendet." });
    }

    /// <summary>Reset password using token from email link.</summary>
    [HttpPost("reset-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Token) || string.IsNullOrWhiteSpace(dto.NewPassword))
            return BadRequest(new { message = "Token und neues Passwort sind erforderlich." });

        if (dto.NewPassword.Length < 6)
            return BadRequest(new { message = "Das Passwort muss mindestens 6 Zeichen lang sein." });

        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(dto.Token)));

        var resetToken = await _db.PasswordResetTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash && !t.IsUsed);

        if (resetToken == null || resetToken.ExpiresAt < DateTime.UtcNow)
            return BadRequest(new { message = "Dieser Link ist ungültig oder abgelaufen. Bitte fordere einen neuen an." });

        resetToken.User.PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.NewPassword, workFactor: 12);
        resetToken.IsUsed = true;
        await _db.SaveChangesAsync();

        _logger.LogInformation("Password reset for user {UserId}", resetToken.UserId);
        return Ok(new { message = "Passwort wurde erfolgreich zurückgesetzt. Du kannst dich jetzt anmelden." });
    }
}

public record TenantAdminLoginDto(string TenantSlug, string Email, string Password);
public record ChangePasswordDto(string CurrentPassword, string NewPassword);
public record ForgotPasswordDto(string TenantSlug, string Email);
public record ResetPasswordDto(string Token, string NewPassword);
