using GentleBook.Api.Data;
using GentleBook.Api.DTOs;
using GentleBook.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Services;

public class EmployeeAuthService
{
    private readonly GentleBookDbContext _context;
    private readonly JwtService _jwt;
    private readonly IConfiguration _config;
    private readonly ILogger<EmployeeAuthService> _logger;

    public EmployeeAuthService(
        GentleBookDbContext context,
        JwtService jwt,
        IConfiguration config,
        ILogger<EmployeeAuthService> logger)
    {
        _context = context;
        _jwt = jwt;
        _config = config;
        _logger = logger;
    }

    public async Task<(bool Success, object? Result, string? ErrorMessage)> LoginAsync(LoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Username) ||
            string.IsNullOrWhiteSpace(request.Password))
            return (false, null, "Benutzername und Passwort sind erforderlich");

        // Usernames are only unique per tenant, so the same username can exist in
        // multiple tenants. Load all candidates, disambiguate via password check and
        // prefer a tenant whose subscription still allows access.
        var candidates = await _context.Employees
            .Include(e => e.Tenant).ThenInclude(t => t.Subscription)
            .Where(e => e.IsActive && e.Tenant.IsActive && e.Username == request.Username.Trim().ToLower())
            .ToListAsync();

        var matching = candidates
            .Where(e => !string.IsNullOrEmpty(e.PasswordHash)
                     && BCrypt.Net.BCrypt.Verify(request.Password, e.PasswordHash))
            .ToList();

        if (matching.Count == 0)
        {
            _logger.LogWarning("Failed login attempt for username: {Username}", request.Username);
            return (false, null, "Ungültiger Benutzername oder Passwort");
        }

        var employee = matching.FirstOrDefault(e => e.Tenant.Subscription?.IsAccessAllowed == true)
                    ?? matching[0];

        // Same check as the TenantAdmin login: block expired tenants at login time
        // instead of letting the employee in and failing on the first API call.
        if (employee.Tenant.Subscription == null || !employee.Tenant.Subscription.IsAccessAllowed)
        {
            _logger.LogWarning("Employee login blocked (subscription inactive) for TenantId={TenantId}", employee.TenantId);
            return (false, null, "Die Testphase Ihres Studios ist abgelaufen. Bitte wenden Sie sich an Ihren Administrator.");
        }

        var token = _jwt.GenerateEmployeeToken(
            employee.Id,
            employee.Name,
            employee.Username!,
            employee.Role,
            employee.TenantId,
            employee.Tenant.Slug);

        _logger.LogInformation("Employee {Name} logged in successfully", employee.Name);

        var result = new
        {
            success = true,
            token = token,
            employee = new
            {
                employee.Id,
                employee.Name,
                employee.Username,
                employee.Role,
                employee.Specialty,
                employee.Location,
                employee.TenantId,
                TenantSlug = employee.Tenant.Slug,
                TenantName = employee.Tenant.Name
            }
        };

        return (true, result, null);
    }

    public async Task<(bool Success, object? Result, string? ErrorMessage)> GetCurrentEmployeeAsync(System.Security.Claims.ClaimsPrincipal user)
    {
        var employeeId = JwtService.GetEmployeeId(user);
        if (employeeId == null)
            return (false, null, "Nicht angemeldet");

        var employee = await _context.Employees
            .Where(e => e.Id == employeeId && e.IsActive)
            .Select(e => new
            {
                e.Id,
                e.Name,
                e.Username,
                e.Role,
                e.Specialty,
                e.Location,
                e.TenantId,
                TenantSlug = e.Tenant.Slug,
                TenantName = e.Tenant.Name
            })
            .FirstOrDefaultAsync();

        if (employee == null)
            return (false, null, "Mitarbeiter nicht gefunden oder deaktiviert");

        return (true, new { success = true, employee }, null);
    }

    public async Task<(bool Success, string? Message, string? ErrorMessage)> ChangePasswordAsync(
        System.Security.Claims.ClaimsPrincipal user,
        ChangePasswordRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.CurrentPassword) ||
            string.IsNullOrWhiteSpace(request.NewPassword))
            return (false, null, "Alle Felder sind erforderlich");

        if (request.NewPassword.Length < 8)
            return (false, null, "Neues Passwort muss mindestens 8 Zeichen haben");

        var employeeId = JwtService.GetEmployeeId(user);
        var employee = await _context.Employees.FindAsync(employeeId);
        if (employee == null)
            return (false, null, "Mitarbeiter nicht gefunden");

        if (string.IsNullOrEmpty(employee.PasswordHash) ||
            !BCrypt.Net.BCrypt.Verify(request.CurrentPassword, employee.PasswordHash))
            return (false, null, "Aktuelles Passwort ist falsch");

        employee.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword, workFactor: 12);
        employee.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return (true, "Passwort erfolgreich geändert", null);
    }

    public async Task<(bool Success, string? Message, string? ErrorMessage)> SetPasswordAsync(SetPasswordRequest request, Guid tenantId)
    {
        if (string.IsNullOrWhiteSpace(request.Username) ||
            string.IsNullOrWhiteSpace(request.Password))
            return (false, null, "Benutzername und Passwort erforderlich");

        var employee = await _context.Employees
            .FirstOrDefaultAsync(e => e.TenantId == tenantId && e.Username == request.Username.Trim().ToLower());

        if (employee == null)
            return (false, null, "Mitarbeiter nicht gefunden");

        employee.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password, workFactor: 12);
        employee.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return (true, $"Passwort für {employee.Name} gesetzt", null);
    }
}
