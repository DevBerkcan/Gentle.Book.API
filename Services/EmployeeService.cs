using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.DTOs;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Services;

public class EmployeeService
{
    private readonly GentleBookDbContext _context;
    private readonly ILogger<EmployeeService> _logger;
    private readonly ITenantContext _tenantContext;

    public EmployeeService(GentleBookDbContext context, ILogger<EmployeeService> logger, ITenantContext tenantContext)
    {
        _context = context;
        _logger = logger;
        _tenantContext = tenantContext;
    }

    // Updated GetAllAsync to optionally filter by service using the junction table
    public async Task<IEnumerable<object>> GetAllAsync(bool activeOnly = true, Guid? serviceId = null)
    {
        var query = _context.Employees
            .Include(e => e.ServiceEmployees)
                .ThenInclude(se => se.Service)
            .AsQueryable();

        if (activeOnly)
            query = query.Where(e => e.IsActive);

        // Filter by service if serviceId is provided using the junction table
        if (serviceId.HasValue)
        {
            query = query.Where(e => e.ServiceEmployees.Any(se => se.ServiceId == serviceId.Value));
        }

        return await query
            .OrderBy(e => e.Name)
            .Select(e => new
            {
                e.Id,
                e.Name,
                e.Role,
                e.Specialty,
                e.Location,
                e.IsActive,
                e.CreatedAt,
                e.UpdatedAt,
                e.Username,
                HasPassword = !string.IsNullOrEmpty(e.PasswordHash),
                // Include assigned service IDs for frontend use
                AssignedServiceIds = e.ServiceEmployees.Select(se => se.ServiceId).ToList()
            })
            .ToListAsync();
    }

    // Updated: Get employees by service ID using the junction table
    public async Task<IEnumerable<object>> GetEmployeesByServiceAsync(Guid serviceId, bool activeOnly = true)
    {
        // First check if service exists
        var service = await _context.Services
            .FirstOrDefaultAsync(s => s.Id == serviceId && s.IsActive);

        if (service == null)
            return new List<object>();

        // Find employees that have this service assigned via the junction table
        var query = _context.Employees
            .Include(e => e.ServiceEmployees)
            .Where(e => e.ServiceEmployees.Any(se => se.ServiceId == serviceId));

        if (activeOnly)
            query = query.Where(e => e.IsActive);

        return await query
            .OrderBy(e => e.Name)
            .Select(e => new
            {
                e.Id,
                e.Name,
                e.Role,
                e.Specialty,
                e.Location,
                e.IsActive,
                e.Username,
                HasPassword = !string.IsNullOrEmpty(e.PasswordHash)
            })
            .ToListAsync();
    }

    // Updated GetByIdAsync to include assigned services via junction table
    public async Task<object?> GetByIdAsync(Guid id)
    {
        var e = await _context.Employees
            .Include(emp => emp.ServiceEmployees)
                .ThenInclude(se => se.Service)
            .FirstOrDefaultAsync(emp => emp.Id == id);

        if (e == null) return null;

        return new
        {
            e.Id,
            e.Name,
            e.Role,
            e.Specialty,
            e.Location,
            e.IsActive,
            e.CreatedAt,
            e.UpdatedAt,
            e.Username,
            HasPassword = !string.IsNullOrEmpty(e.PasswordHash),
            // Include assigned services
            AssignedServices = e.ServiceEmployees
                .Where(se => se.Service.IsActive)
                .Select(se => new
                {
                    se.Service.Id,
                    se.Service.Name,
                    se.Service.DurationMinutes,
                    se.Service.Price
                }).ToList()
        };
    }

    // Updated: Get employees with their assigned services (for admin panel)
    public async Task<IEnumerable<EmployeeWithServicesDto>> GetEmployeesWithServicesAsync(bool activeOnly = true)
    {
        var query = _context.Employees
            .Include(e => e.ServiceEmployees)
                .ThenInclude(se => se.Service)
            .AsQueryable();

        if (activeOnly)
            query = query.Where(e => e.IsActive);

        return await query
            .OrderBy(e => e.Name)
            .Select(e => new EmployeeWithServicesDto(
                e.Id,
                e.Name,
                e.Role,
                e.Specialty,
                e.IsActive,
                e.Location,
                e.ServiceEmployees
                    .Where(se => se.Service.IsActive)
                    .Select(se => new ServiceBasicDto(
                        se.Service.Id,
                        se.Service.Name,
                        se.Service.DurationMinutes,
                        se.Service.Price
                    )).ToList()
            ))
            .ToListAsync();
    }

    // Keep existing GetStatsAsync method
    public async Task<object?> GetStatsAsync(Guid id, DateOnly? from, DateOnly? to)
    {
        var exists = await _context.Employees.AnyAsync(e => e.Id == id);
        if (!exists) return null;

        var bookingsQ = _context.Bookings
            .Include(b => b.Service)
            .Where(b => b.EmployeeId == id);

        if (from.HasValue) bookingsQ = bookingsQ.Where(b => b.BookingDate >= from.Value);
        if (to.HasValue) bookingsQ = bookingsQ.Where(b => b.BookingDate <= to.Value);

        var bookings = await bookingsQ.ToListAsync();
        var blockedCount = await _context.BlockedTimeSlots.CountAsync(b => b.EmployeeId == id);

        return new
        {
            EmployeeId = id,
            TotalBookings = bookings.Count,
            BlockedSlots = blockedCount,
        };
    }

    // Keep existing CreateAsync method
    public async Task<(bool Success, object? Employee, string? ErrorMessage)> CreateAsync(CreateEmployeeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return (false, null, "Name ist erforderlich");

        if (!string.IsNullOrWhiteSpace(request.Username))
        {
            var username = request.Username.Trim().ToLower();
            if (await _context.Employees.AnyAsync(e => e.Username == username))
                return (false, null, "Benutzername bereits vergeben");
        }

        var employee = new Employee
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantContext.TenantId ?? throw new InvalidOperationException("TenantId fehlt"),
            Name = request.Name.Trim(),
            Role = request.Role?.Trim() ?? "Mitarbeiterin",
            Specialty = request.Specialty?.Trim(),
            Location = request.Location?.Trim(),
            IsActive = true,
            Username = request.Username?.Trim().ToLower(),
            PasswordHash = !string.IsNullOrWhiteSpace(request.Password)
                               ? BCrypt.Net.BCrypt.HashPassword(request.Password, workFactor: 12)
                               : null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        _context.Employees.Add(employee);
        await _context.SaveChangesAsync();
        _logger.LogInformation("Created employee {Name}", employee.Name);

        var result = new
        {
            employee.Id,
            employee.Name,
            employee.Role,
            employee.Specialty,
            employee.Location,
            employee.IsActive,
            employee.Username,
            HasPassword = employee.PasswordHash != null,
        };

        return (true, result, null);
    }

    // Keep existing UpdateAsync method
    public async Task<(bool Success, object? Employee, string? ErrorMessage)> UpdateAsync(Guid id, UpdateEmployeeRequest request)
    {
        var employee = await _context.Employees.FindAsync(id);
        if (employee == null)
            return (false, null, "Mitarbeiter nicht gefunden");

        if (!string.IsNullOrWhiteSpace(request.Username))
        {
            var username = request.Username.Trim().ToLower();
            if (await _context.Employees.AnyAsync(e => e.Username == username && e.Id != id))
                return (false, null, "Benutzername bereits vergeben");
            employee.Username = username;
        }

        if (!string.IsNullOrWhiteSpace(request.Name)) employee.Name = request.Name.Trim();
        if (!string.IsNullOrWhiteSpace(request.Role)) employee.Role = request.Role.Trim();
        if (request.Specialty != null) employee.Specialty = request.Specialty?.Trim();
        if (request.Location != null) employee.Location = request.Location?.Trim();
        if (request.IsActive.HasValue) employee.IsActive = request.IsActive.Value;

        if (!string.IsNullOrWhiteSpace(request.NewPassword))
        {
            employee.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword, workFactor: 12);
        }

        employee.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        var result = new
        {
            employee.Id,
            employee.Name,
            employee.Role,
            employee.Specialty,
            employee.Location,
            employee.IsActive,
            employee.Username,
            HasPassword = employee.PasswordHash != null,
        };

        return (true, result, null);
    }

    // Keep existing ToggleActiveAsync method
    public async Task<(bool Success, object? Result, string? ErrorMessage)> ToggleActiveAsync(Guid id)
    {
        var employee = await _context.Employees.FindAsync(id);
        if (employee == null)
            return (false, null, "Mitarbeiter nicht gefunden");

        employee.IsActive = !employee.IsActive;
        employee.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return (true, new { employee.Id, employee.IsActive }, null);
    }

    // Keep existing DeleteAsync method
    public async Task<(bool Success, string? ErrorMessage)> DeleteAsync(Guid id)
    {
        if (await _context.Bookings.AnyAsync(b => b.EmployeeId == id))
            return (false, "Mitarbeiter hat Buchungen und kann nicht gelöscht werden. Bitte deaktivieren.");

        var employee = await _context.Employees.FindAsync(id);
        if (employee == null)
            return (false, "Mitarbeiter nicht gefunden");

        _context.Employees.Remove(employee);
        await _context.SaveChangesAsync();
        return (true, null);
    }
}