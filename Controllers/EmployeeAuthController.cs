using GentleBook.Api.Data;
using GentleBook.Api.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using GentleBook.Api.Services;

namespace GentleBook.Api.Controllers;

[ApiController]
[Route("api/employee-auth")]
public class EmployeeAuthController : ControllerBase
{
    private readonly EmployeeAuthService _authService;
    private readonly ILogger<EmployeeAuthController> _logger;

    public EmployeeAuthController(
        EmployeeAuthService authService,
        ILogger<EmployeeAuthController> logger)
    {
        _authService = authService;
        _logger = logger;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth-limit")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var (success, result, errorMessage) = await _authService.LoginAsync(request);

        if (!success)
            return Unauthorized(new { message = errorMessage });

        return Ok(result);
    }

    [HttpPost("logout")]
    [Authorize]
    public IActionResult Logout()
    {
        return Ok(new { success = true, message = "Erfolgreich abgemeldet" });
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me()
    {
        var (success, result, errorMessage) = await _authService.GetCurrentEmployeeAsync(User);

        if (!success)
            return Unauthorized(new { message = errorMessage });

        return Ok(result);
    }

    [HttpPut("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        var (success, message, errorMessage) = await _authService.ChangePasswordAsync(User, request);

        if (!success)
            return BadRequest(new { message = errorMessage });

        return Ok(new { success = true, message });
    }

    [HttpPost("set-password")]
    [Authorize]
    public async Task<IActionResult> SetPassword([FromBody] SetPasswordRequest request)
    {
        // Only a TenantAdmin may set employee passwords, and only within the own tenant.
        var role = JwtService.GetRole(User);
        var tenantId = JwtService.GetTenantId(User);
        if (role != "TenantAdmin" || tenantId == null)
            return Forbid();

        var (success, message, errorMessage) = await _authService.SetPasswordAsync(request, tenantId.Value);

        if (!success)
        {
            if (errorMessage == "Mitarbeiter nicht gefunden")
                return NotFound(new { message = errorMessage });
            return BadRequest(new { message = errorMessage });
        }

        return Ok(new { success = true, message });
    }
}