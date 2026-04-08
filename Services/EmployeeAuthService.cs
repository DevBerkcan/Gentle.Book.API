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

        var employee = await _context.Employees
            .Where(e => e.IsActive && e.Username == request.Username.Trim().ToLower())
            .FirstOrDefaultAsync();

        bool passwordOk = employee != null
            && !string.IsNullOrEmpty(employee.PasswordHash)
            && BCrypt.Net.BCrypt.Verify(request.Password, employee.PasswordHash);

        if (!passwordOk || employee == null)
        {
            _logger.LogWarning("Failed login attempt for username: {Username}", request.Username);
            return (false, null, "Ungültiger Benutzername oder Passwort");
        }

        var token = _jwt.GenerateToken(
            employee.Id, employee.Name, employee.Username!, employee.Role);

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
                employee.Specialty
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
            .Select(e => new { e.Id, e.Name, e.Username, e.Role, e.Specialty })
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

    public async Task<(bool Success, string? Message, string? ErrorMessage)> SetPasswordAsync(SetPasswordRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Username) ||
            string.IsNullOrWhiteSpace(request.Password))
            return (false, null, "Benutzername und Passwort erforderlich");

        var employee = await _context.Employees
            .FirstOrDefaultAsync(e => e.Username == request.Username.Trim().ToLower());

        if (employee == null)
            return (false, null, "Mitarbeiter nicht gefunden");

        employee.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password, workFactor: 12);
        employee.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return (true, $"Passwort für {employee.Name} gesetzt", null);
    }
}