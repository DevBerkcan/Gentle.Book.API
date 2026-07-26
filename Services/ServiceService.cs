using GentleBook.Api.Configuration;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.DTOs;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Services;

public class ServiceService
{
    private readonly GentleBookDbContext _context;
    private readonly ILogger<ServiceService> _logger;
    private readonly ITenantContext _tenantContext;

    public ServiceService(GentleBookDbContext context, ILogger<ServiceService> logger, ITenantContext tenantContext)
    {
        _context = context;
        _logger = logger;
        _tenantContext = tenantContext;
    }

    private Guid RequireTenantId()
        => _tenantContext.TenantId ?? throw new InvalidOperationException("TenantId fehlt");

    private async Task<Guid?> ResolveTenantIdAsync(string? tenantSlug)
    {
        if (_tenantContext.TenantId.HasValue)
            return _tenantContext.TenantId.Value;

        if (string.IsNullOrWhiteSpace(tenantSlug))
            return null;

        return await _context.Tenants
            .Where(t => t.Slug == tenantSlug.Trim().ToLower() && t.IsActive)
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync();
    }

    // ── PUBLIC METHODS (for booking widget) ─────────────────────────

    public async Task<List<ServiceDto>> GetServicesAsync(Guid? employeeId = null, string? tenantSlug = null)
    {
        var tenantId = await ResolveTenantIdAsync(tenantSlug);
        if (!tenantId.HasValue)
            return new List<ServiceDto>();

        var query = _context.Services
            .Include(s => s.ServiceEmployees)
            .Where(s => s.TenantId == tenantId.Value && s.IsActive)
            .AsQueryable();

        // For employee portal - filter by employee
        if (employeeId.HasValue)
        {
            query = query.Where(s => s.ServiceEmployees.Any(se => se.EmployeeId == employeeId.Value));
        }

        var services = await query
            .OrderBy(s => s.DisplayOrder)
            .Select(s => new ServiceDto(
                s.Id,
                s.Name,
                s.Description,
                s.DurationMinutes,
                s.Price,
                s.DisplayOrder,
                s.Currency,
                s.LocationId,
                s.Location != null ? s.Location.Name : null
            ))
            .ToListAsync();

        _logger.LogInformation("Retrieved {Count} services for employee {EmployeeId}",
            services.Count, employeeId);
        return services;
    }

    public async Task<List<ServiceCategoryDto>> GetServiceCategoriesAsync(Guid? employeeId = null, string? tenantSlug = null)
    {
        var tenantId = await ResolveTenantIdAsync(tenantSlug);
        if (!tenantId.HasValue)
            return new List<ServiceCategoryDto>();

        var categories = await _context.ServiceCategories
            .Include(c => c.Services)
                .ThenInclude(s => s.ServiceEmployees)
            .Where(c => c.TenantId == tenantId.Value && c.IsActive)
            .OrderBy(c => c.DisplayOrder)
            .Select(c => new ServiceCategoryDto(
                c.Id,
                c.Name,
                c.Description,
                c.DisplayOrder,
                c.IsActive,
                c.Services
                    .Where(s => s.IsActive &&
                           (!employeeId.HasValue || s.ServiceEmployees.Any(se => se.EmployeeId == employeeId.Value)))
                    .OrderBy(s => s.DisplayOrder)
                    .Select(s => new ServiceDto(
                        s.Id,
                        s.Name,
                        s.Description,
                        s.DurationMinutes,
                        s.Price,
                        s.DisplayOrder,
                        s.Currency,
                        s.LocationId,
                        s.Location != null ? s.Location.Name : null
                    ))
                    .ToList()
            ))
            .ToListAsync();

        if (employeeId.HasValue)
        {
            categories = categories.Where(c => c.Services.Any()).ToList();
        }

        _logger.LogInformation("Retrieved {Count} service categories for employee {EmployeeId}",
            categories.Count, employeeId);
        return categories;
    }

    public async Task<(List<ServiceDto>? Services, bool CategoryExists)> GetServicesByCategoryAsync(
        Guid categoryId,
        Guid? employeeId = null,
        string? tenantSlug = null)
    {
        var tenantId = await ResolveTenantIdAsync(tenantSlug);
        if (!tenantId.HasValue)
            return (null, false);

        var category = await _context.ServiceCategories
            .FirstOrDefaultAsync(c => c.Id == categoryId && c.TenantId == tenantId.Value && c.IsActive);

        if (category == null)
            return (null, false);

        var query = _context.Services
            .Include(s => s.ServiceEmployees)
            .Where(s => s.TenantId == tenantId.Value && s.CategoryId == categoryId && s.IsActive);

        if (employeeId.HasValue)
        {
            query = query.Where(s => s.ServiceEmployees.Any(se => se.EmployeeId == employeeId.Value));
        }

        var services = await query
            .OrderBy(s => s.DisplayOrder)
            .Select(s => new ServiceDto(
                s.Id,
                s.Name,
                s.Description,
                s.DurationMinutes,
                s.Price,
                s.DisplayOrder,
                s.Currency,
                s.LocationId,
                s.Location != null ? s.Location.Name : null
            ))
            .ToListAsync();

        _logger.LogInformation("Retrieved {Count} services for category {CategoryId} and employee {EmployeeId}",
            services.Count, categoryId, employeeId);

        return (services, true);
    }

    public async Task<ServiceWithCategoryDto?> GetServiceDetailsAsync(Guid id, Guid? employeeId = null, string? tenantSlug = null)
    {
        var tenantId = await ResolveTenantIdAsync(tenantSlug);
        if (!tenantId.HasValue)
            return null;

        var query = _context.Services
            .Include(s => s.Category)
            .Include(s => s.ServiceEmployees)
                .ThenInclude(se => se.Employee)
            .Where(s => s.Id == id && s.TenantId == tenantId.Value && s.IsActive);

        if (employeeId.HasValue)
        {
            query = query.Where(s => s.ServiceEmployees.Any(se => se.EmployeeId == employeeId.Value));
        }

        var service = await query
            .Select(s => new ServiceWithCategoryDto(
                s.Id,
                s.Name,
                s.Description,
                s.DurationMinutes,
                s.Price,
                s.DisplayOrder,
                s.CategoryId,
                s.Category.Name,
                s.Currency,
                s.ServiceEmployees.Select(se => new EmployeeBasicDto(
                    se.Employee.Id,
                    se.Employee.Name,
                    se.Employee.Role,
                    se.Employee.Specialty
                )).ToList(),
                s.LocationId,
                s.Location != null ? s.Location.Name : null
            ))
            .FirstOrDefaultAsync();

        return service;
    }

    public async Task<List<ServiceDto>> GetEmployeeServicesAsync(Guid employeeId)
    {
        var tenantId = RequireTenantId();

        var services = await _context.Services
            .Include(s => s.ServiceEmployees)
            .Where(s => s.TenantId == tenantId && s.IsActive && s.ServiceEmployees.Any(se => se.EmployeeId == employeeId && se.Employee.TenantId == tenantId))
            .OrderBy(s => s.DisplayOrder)
            .Select(s => new ServiceDto(
                s.Id,
                s.Name,
                s.Description,
                s.DurationMinutes,
                s.Price,
                s.DisplayOrder,
                s.Currency,
                s.LocationId,
                s.Location != null ? s.Location.Name : null
            ))
            .ToListAsync();

        _logger.LogInformation("Retrieved {Count} services for employee {EmployeeId}",
            services.Count, employeeId);
        return services;
    }

    public async Task<List<ServiceCategoryDto>> GetAllCategoriesAsync()
    {
        var tenantId = RequireTenantId();

        var categories = await _context.ServiceCategories
            .Include(c => c.Services)
            .Where(c => c.TenantId == tenantId)
            .OrderBy(c => c.DisplayOrder)
            .Select(c => new ServiceCategoryDto(
                c.Id,
                c.Name,
                c.Description,
                c.DisplayOrder,
                c.IsActive,
                c.Services
                    .Where(s => s.IsActive)
                    .OrderBy(s => s.DisplayOrder)
                    .Select(s => new ServiceDto(
                        s.Id,
                        s.Name,
                        s.Description,
                        s.DurationMinutes,
                        s.Price,
                        s.DisplayOrder,
                        s.Currency,
                        s.LocationId,
                        s.Location != null ? s.Location.Name : null
                    ))
                    .ToList()
            ))
            .ToListAsync();

        return categories;
    }

    public async Task<object> GetServicesSummaryAsync(Guid? employeeId = null, string? tenantSlug = null)
    {
        var resolvedTenantId = await ResolveTenantIdAsync(tenantSlug);
        if (!resolvedTenantId.HasValue)
        {
            return new
            {
                TotalServices = 0,
                TotalCategories = 0,
                CategoryBreakdown = Array.Empty<object>(),
                ForEmployee = employeeId,
                LastUpdated = DateTime.UtcNow
            };
        }

        var tenantId = resolvedTenantId.Value;

        var query = _context.Services
            .Include(s => s.ServiceEmployees)
            .Where(s => s.TenantId == tenantId && s.IsActive);

        if (employeeId.HasValue)
        {
            query = query.Where(s => s.ServiceEmployees.Any(se => se.EmployeeId == employeeId.Value));
        }

        var totalServices = await query.CountAsync();
        var totalCategories = await _context.ServiceCategories.CountAsync(c => c.TenantId == tenantId && c.IsActive);

        var categoryBreakdown = await _context.ServiceCategories
            .Where(c => c.TenantId == tenantId && c.IsActive)
            .Select(c => new
            {
                CategoryName = c.Name,
                ServiceCount = c.Services.Count(s => s.IsActive &&
                    (!employeeId.HasValue || s.ServiceEmployees.Any(se => se.EmployeeId == employeeId.Value))),
                AveragePrice = c.Services
                    .Where(s => s.IsActive &&
                        (!employeeId.HasValue || s.ServiceEmployees.Any(se => se.EmployeeId == employeeId.Value)))
                    .Average(s => (double?)s.Price) ?? 0,
                MinPrice = c.Services
                    .Where(s => s.IsActive &&
                        (!employeeId.HasValue || s.ServiceEmployees.Any(se => se.EmployeeId == employeeId.Value)))
                    .Min(s => (decimal?)s.Price) ?? 0,
                MaxPrice = c.Services
                    .Where(s => s.IsActive &&
                        (!employeeId.HasValue || s.ServiceEmployees.Any(se => se.EmployeeId == employeeId.Value)))
                    .Max(s => (decimal?)s.Price) ?? 0
            })
            .Where(c => c.ServiceCount > 0)
            .ToListAsync();

        return new
        {
            TotalServices = totalServices,
            TotalCategories = totalCategories,
            CategoryBreakdown = categoryBreakdown,
            ForEmployee = employeeId,
            LastUpdated = DateTime.UtcNow
        };
    }

    // ── SINGLE ASSIGNMENT METHODS (keep for backward compatibility) ──

    public async Task<bool> AssignServiceToEmployeeAsync(Guid serviceId, Guid employeeId)
    {
        var tenantId = RequireTenantId();

        var service = await _context.Services
            .Include(s => s.ServiceEmployees)
            .FirstOrDefaultAsync(s => s.Id == serviceId && s.TenantId == tenantId && s.IsActive);

        if (service == null)
            throw new ArgumentException("Service not found");

        var employee = await _context.Employees
            .FirstOrDefaultAsync(e => e.Id == employeeId && e.TenantId == tenantId && e.IsActive);

        if (employee == null)
            throw new ArgumentException("Employee not found");

        // Check if already assigned
        if (service.ServiceEmployees.Any(se => se.EmployeeId == employeeId))
            throw new InvalidOperationException("Service already assigned to this employee");

        service.ServiceEmployees.Add(new ServiceEmployee
        {
            ServiceId = serviceId,
            EmployeeId = employeeId,
            CreatedAt = DateTime.UtcNow
        });
        service.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        _logger.LogInformation("Service {ServiceId} assigned to employee {EmployeeId}",
            serviceId, employeeId);

        return true;
    }

    public async Task<bool> RemoveServiceFromEmployeeAsync(Guid serviceId, Guid employeeId)
    {
        var tenantId = RequireTenantId();

        var serviceEmployee = await _context.ServiceEmployees
            .FirstOrDefaultAsync(se =>
                se.ServiceId == serviceId &&
                se.EmployeeId == employeeId &&
                se.Service.TenantId == tenantId &&
                se.Employee.TenantId == tenantId);

        if (serviceEmployee == null)
            throw new ArgumentException("Assignment not found");

        _context.ServiceEmployees.Remove(serviceEmployee);

        var service = await _context.Services.FirstOrDefaultAsync(s => s.Id == serviceId && s.TenantId == tenantId);
        if (service != null)
        {
            service.UpdatedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync();

        _logger.LogInformation("Service {ServiceId} removed from employee {EmployeeId}", serviceId, employeeId);

        return true;
    }

    // ── ADMIN METHODS ──────────────────────────────────────────────

    public async Task<List<AdminServiceDto>> GetAllServicesAdminAsync()
    {
        var tenantId = RequireTenantId();

        var services = await _context.Services
            .Include(s => s.Category)
            .Include(s => s.ServiceEmployees)
                .ThenInclude(se => se.Employee)
            .Where(s => s.TenantId == tenantId)
            .OrderBy(s => s.Category.DisplayOrder)
            .ThenBy(s => s.DisplayOrder)
            .Select(s => new AdminServiceDto(
                s.Id,
                s.Name,
                s.Description,
                s.DurationMinutes,
                s.BufferTimeMinutes,
                s.Price,
                s.DisplayOrder,
                s.CategoryId,
                s.Category.Name,
                s.Currency,
                s.ServiceEmployees.Select(se => new EmployeeBasicDto(
                    se.Employee.Id,
                    se.Employee.Name,
                    se.Employee.Role,
                    se.Employee.Specialty
                )).ToList(),
                s.IsActive,
                s.LocationId,
                s.Location != null ? s.Location.Name : null
            ))
            .ToListAsync();

        _logger.LogInformation("Retrieved {Count} services for admin", services.Count);
        return services;
    }

    public async Task<AdminServiceDto?> GetServiceByIdAdminAsync(Guid id)
    {
        var tenantId = RequireTenantId();

        var service = await _context.Services
            .Include(s => s.Category)
            .Include(s => s.Location)
            .Include(s => s.ServiceEmployees)
                .ThenInclude(se => se.Employee)
            .FirstOrDefaultAsync(s => s.Id == id && s.TenantId == tenantId);

        if (service == null)
            return null;

        return new AdminServiceDto(
            service.Id,
            service.Name,
            service.Description,
            service.DurationMinutes,
            service.BufferTimeMinutes,
            service.Price,
            service.DisplayOrder,
            service.CategoryId,
            service.Category.Name,
            service.Currency,
            service.ServiceEmployees.Select(se => new EmployeeBasicDto(
                se.Employee.Id,
                se.Employee.Name,
                se.Employee.Role,
                se.Employee.Specialty
            )).ToList(),
            service.IsActive,
            service.LocationId,
            service.Location?.Name
        );
    }

    public async Task<AdminServiceDto> CreateServiceAsync(CreateServiceDto dto)
    {
        var tenantId = RequireTenantId();
        var location = dto.LocationId.HasValue
            ? await _context.BusinessLocations.FirstOrDefaultAsync(item =>
                item.Id == dto.LocationId.Value && item.TenantId == tenantId && item.IsActive)
            : await _context.BusinessLocations.FirstOrDefaultAsync(item =>
                item.TenantId == tenantId && item.IsDefault && item.IsActive);
        if (dto.LocationId.HasValue && location == null)
            throw new ArgumentException("Standort nicht gefunden");
        var tenantCurrency = location?.Currency ?? await _context.TenantSettings
            .Where(s => s.TenantId == tenantId)
            .Select(s => s.DefaultCurrency)
            .FirstOrDefaultAsync() ?? "EUR";

        // Validate category
        var category = await _context.ServiceCategories.FirstOrDefaultAsync(c => c.Id == dto.CategoryId && c.TenantId == tenantId);
        if (category == null)
            throw new ArgumentException("Kategorie nicht gefunden");

        // Enforce plan limits
        if (tenantId != Guid.Empty)
        {
            var subscription = await _context.Subscriptions.FirstOrDefaultAsync(s => s.TenantId == tenantId);
            if (subscription != null)
            {
                var limits = PlanLimits.Get(subscription.Plan);
                if (!PlanLimits.IsUnlimited(limits.MaxServices))
                {
                    var currentCount = await _context.Services.CountAsync(s => s.TenantId == tenantId && s.IsActive);
                    if (currentCount >= limits.MaxServices)
                        throw new InvalidOperationException($"Ihr Plan erlaubt maximal {limits.MaxServices} aktive Services. Bitte upgraden Sie Ihren Plan.");
                }
            }
        }

        var service = new Service
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = dto.Name,
            Description = dto.Description,
            DurationMinutes = dto.DurationMinutes,
            BufferTimeMinutes = dto.BufferTimeMinutes,
            Price = dto.Price,
            Currency = tenantCurrency.ToUpperInvariant(),
            LocationId = location?.Id,
            DisplayOrder = dto.DisplayOrder,
            CategoryId = dto.CategoryId,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.Services.Add(service);

        // Add employee assignments if provided
        if (dto.EmployeeIds != null && dto.EmployeeIds.Any())
        {
            foreach (var employeeId in dto.EmployeeIds.Distinct())
            {
                var employee = await _context.Employees.FirstOrDefaultAsync(e => e.Id == employeeId && e.TenantId == tenantId);
                if (employee == null)
                    throw new ArgumentException($"Mitarbeiter mit ID {employeeId} nicht gefunden");

                _context.ServiceEmployees.Add(new ServiceEmployee
                {
                    ServiceId = service.Id,
                    EmployeeId = employeeId,
                    CreatedAt = DateTime.UtcNow
                });
            }
        }

        await _context.SaveChangesAsync();

        _logger.LogInformation("Created new service: {ServiceName}", service.Name);

        return await GetServiceByIdAdminAsync(service.Id)
            ?? throw new InvalidOperationException("Failed to retrieve created service");
    }

    public async Task<AdminServiceDto> UpdateServiceAsync(Guid id, UpdateServiceDto dto)
    {
        var tenantId = RequireTenantId();
        var location = dto.LocationId.HasValue
            ? await _context.BusinessLocations.FirstOrDefaultAsync(item =>
                item.Id == dto.LocationId.Value && item.TenantId == tenantId && item.IsActive)
            : await _context.BusinessLocations.FirstOrDefaultAsync(item =>
                item.TenantId == tenantId && item.IsDefault && item.IsActive);
        if (dto.LocationId.HasValue && location == null)
            throw new ArgumentException("Standort nicht gefunden");
        var tenantCurrency = location?.Currency ?? await _context.TenantSettings
            .Where(s => s.TenantId == tenantId)
            .Select(s => s.DefaultCurrency)
            .FirstOrDefaultAsync() ?? "EUR";

        var service = await _context.Services
            .Include(s => s.ServiceEmployees)
            .FirstOrDefaultAsync(s => s.Id == id && s.TenantId == tenantId);

        if (service == null)
            throw new ArgumentException("Service nicht gefunden");

        // Validate category
        var category = await _context.ServiceCategories.FirstOrDefaultAsync(c => c.Id == dto.CategoryId && c.TenantId == tenantId);
        if (category == null)
            throw new ArgumentException("Kategorie nicht gefunden");

        // Check if service has bookings and is being deactivated
        if (!dto.IsActive && service.IsActive)
        {
            var hasFutureBookings = await _context.Bookings
                .AnyAsync(b => b.ServiceId == id &&
                              b.TenantId == tenantId &&
                              b.BookingDate >= DateOnly.FromDateTime(DateTime.UtcNow) &&
                              b.Status != BookingStatus.Cancelled);

            if (hasFutureBookings)
                throw new InvalidOperationException("Kann Service mit zukünftigen Buchungen nicht deaktivieren");
        }

        // Update service properties
        service.Name = dto.Name;
        service.Description = dto.Description;
        service.DurationMinutes = dto.DurationMinutes;
        service.BufferTimeMinutes = dto.BufferTimeMinutes;
        service.Price = dto.Price;
        service.Currency = tenantCurrency.ToUpperInvariant();
        service.LocationId = location?.Id;
        service.DisplayOrder = dto.DisplayOrder;
        service.CategoryId = dto.CategoryId;
        service.IsActive = dto.IsActive;
        service.UpdatedAt = DateTime.UtcNow;

        // Update employee assignments if provided
        if (dto.EmployeeIds != null)
        {
            // Remove old assignments
            _context.ServiceEmployees.RemoveRange(service.ServiceEmployees);

            // Add new assignments
            foreach (var employeeId in dto.EmployeeIds.Distinct())
            {
                var employee = await _context.Employees.FirstOrDefaultAsync(e => e.Id == employeeId && e.TenantId == tenantId);
                if (employee == null)
                    throw new ArgumentException($"Mitarbeiter mit ID {employeeId} nicht gefunden");

                _context.ServiceEmployees.Add(new ServiceEmployee
                {
                    ServiceId = service.Id,
                    EmployeeId = employeeId,
                    CreatedAt = DateTime.UtcNow
                });
            }
        }

        await _context.SaveChangesAsync();

        _logger.LogInformation("Updated service: {ServiceName}", service.Name);

        return await GetServiceByIdAdminAsync(service.Id)
            ?? throw new InvalidOperationException("Failed to retrieve updated service");
    }

    public async Task<bool> DeleteServiceAsync(Guid id)
    {
        var tenantId = RequireTenantId();

        var service = await _context.Services
            .Include(s => s.Bookings)
            .Include(s => s.ServiceEmployees)
            .FirstOrDefaultAsync(s => s.Id == id && s.TenantId == tenantId);

        if (service == null)
            throw new ArgumentException("Service nicht gefunden");

        // Block deletion if future non-cancelled bookings exist
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var futureBookingCount = service.Bookings?.Count(b =>
            b.BookingDate >= today &&
            b.Status != BookingStatus.Cancelled) ?? 0;

        if (futureBookingCount > 0)
            throw new InvalidOperationException(
                $"Dieser Service hat {futureBookingCount} offene zukünftige Buchung(en). Bitte deaktivieren Sie den Service statt ihn zu löschen, oder stornieren Sie zuerst alle Buchungen.");

        // Use a transaction to ensure all operations succeed or fail together
        using var transaction = await _context.Database.BeginTransactionAsync();
        try
        {
            // Log how many items will be deleted
            int bookingsCount = service.Bookings?.Count ?? 0;
            int serviceEmployeesCount = service.ServiceEmployees?.Count ?? 0;

            _logger.LogWarning("Deleting service {ServiceName} with {BookingsCount} bookings and {EmployeeCount} employee associations",
                service.Name, bookingsCount, serviceEmployeesCount);

            // 1. First delete all bookings that use this service
            if (service.Bookings != null && service.Bookings.Any())
            {
                _context.Bookings.RemoveRange(service.Bookings);
            }

            // 2. Delete ServiceEmployees entries (though cascade delete would handle this, 
            // but explicit is clearer)
            if (service.ServiceEmployees != null && service.ServiceEmployees.Any())
            {
                _context.ServiceEmployees.RemoveRange(service.ServiceEmployees);
            }

            // 3. Delete any other related records if needed
            // For example, if you have EmailLogs related to these bookings, you might want to delete them too
            var bookingIds = service.Bookings?.Select(b => b.Id).ToList();
            if (bookingIds != null && bookingIds.Any())
            {
                // Delete email logs for these bookings
                var emailLogs = await _context.EmailLogs
                    .Where(e => e.TenantId == tenantId && bookingIds.Contains(e.BookingId ?? Guid.Empty))
                    .ToListAsync();

                if (emailLogs.Any())
                {
                    _context.EmailLogs.RemoveRange(emailLogs);
                }
            }

            // 4. Finally delete the service
            _context.Services.Remove(service);

            // Save all changes
            await _context.SaveChangesAsync();

            // Commit the transaction
            await transaction.CommitAsync();

            _logger.LogInformation("Successfully deleted service: {ServiceName} with all related records (Bookings: {BookingsCount}, Employee associations: {EmployeeCount})",
                service.Name, bookingsCount, serviceEmployeesCount);

            return true;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _logger.LogError(ex, "Error deleting service {ServiceId}", id);
            throw new InvalidOperationException($"Fehler beim Löschen des Services: {ex.Message}", ex);
        }
    }

    public async Task<bool> ToggleServiceActiveAsync(Guid id)
    {
        var tenantId = RequireTenantId();

        var service = await _context.Services
            .Include(s => s.Bookings)
            .FirstOrDefaultAsync(s => s.Id == id && s.TenantId == tenantId);

        if (service == null)
            throw new ArgumentException("Service nicht gefunden");

        // If trying to deactivate, check for future bookings
        if (service.IsActive)
        {
            var hasFutureBookings = await _context.Bookings
                .AnyAsync(b => b.ServiceId == id &&
                              b.TenantId == tenantId &&
                              b.BookingDate >= DateOnly.FromDateTime(DateTime.UtcNow) &&
                              b.Status != BookingStatus.Cancelled);

            if (hasFutureBookings)
                throw new InvalidOperationException("Kann Service mit zukünftigen Buchungen nicht deaktivieren");
        }

        service.IsActive = !service.IsActive;
        service.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        _logger.LogInformation("Toggled service active status: {ServiceName} -> {IsActive}",
            service.Name, service.IsActive);

        return service.IsActive;
    }

    // ── CATEGORY ADMIN METHODS ─────────────────────────────────────

    public async Task<List<AdminServiceCategoryDto>> GetAllCategoriesAdminAsync()
    {
        var tenantId = RequireTenantId();

        var categories = await _context.ServiceCategories
            .Include(c => c.Services)
                .ThenInclude(s => s.ServiceEmployees)
                .ThenInclude(se => se.Employee)
            .Where(c => c.TenantId == tenantId)
            .OrderBy(c => c.DisplayOrder)
            .Select(c => new AdminServiceCategoryDto(
                c.Id,
                c.Name,
                c.Description,
                c.DisplayOrder,
                c.IsActive,
                c.Services.Select(s => new AdminServiceDto(
                    s.Id,
                    s.Name,
                    s.Description,
                    s.DurationMinutes,
                    s.BufferTimeMinutes,
                    s.Price,
                    s.DisplayOrder,
                    s.CategoryId,
                    c.Name,
                    s.Currency,
                    s.ServiceEmployees.Select(se => new EmployeeBasicDto(
                        se.Employee.Id,
                        se.Employee.Name,
                        se.Employee.Role,
                        se.Employee.Specialty
                    )).ToList(),
                    s.IsActive,
                    s.LocationId,
                    s.Location != null ? s.Location.Name : null
                )).ToList()
            ))
            .ToListAsync();

        return categories;
    }

    public async Task<AdminServiceCategoryDto?> GetCategoryByIdAdminAsync(Guid id)
    {
        var tenantId = RequireTenantId();

        var category = await _context.ServiceCategories
            .Include(c => c.Services)
                .ThenInclude(s => s.ServiceEmployees)
                .ThenInclude(se => se.Employee)
            .Include(c => c.Services)
                .ThenInclude(s => s.Location)
            .FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tenantId);

        if (category == null)
            return null;

        return new AdminServiceCategoryDto(
            category.Id,
            category.Name,
            category.Description,
            category.DisplayOrder,
            category.IsActive,
            category.Services.Select(s => new AdminServiceDto(
                s.Id,
                s.Name,
                s.Description,
                s.DurationMinutes,
                s.BufferTimeMinutes,
                s.Price,
                s.DisplayOrder,
                s.CategoryId,
                category.Name,
                s.Currency,
                s.ServiceEmployees.Select(se => new EmployeeBasicDto(
                    se.Employee.Id,
                    se.Employee.Name,
                    se.Employee.Role,
                    se.Employee.Specialty
                )).ToList(),
                s.IsActive,
                s.LocationId,
                s.Location?.Name
            )).ToList()
        );
    }

    public async Task<AdminServiceCategoryDto> CreateCategoryAsync(CreateCategoryDto dto)
    {
        var tenantId = RequireTenantId();

        // Check if category with same name exists
        var existing = await _context.ServiceCategories
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Name.ToLower() == dto.Name.ToLower());

        if (existing != null)
            throw new ArgumentException("Eine Kategorie mit diesem Namen existiert bereits");

        var category = new ServiceCategory
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = dto.Name,
            Description = dto.Description,
            DisplayOrder = dto.DisplayOrder,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.ServiceCategories.Add(category);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Created new category: {CategoryName}", category.Name);

        return await GetCategoryByIdAdminAsync(category.Id)
            ?? throw new InvalidOperationException("Failed to retrieve created category");
    }

    public async Task<AdminServiceCategoryDto> UpdateCategoryAsync(Guid id, UpdateCategoryDto dto)
    {
        var tenantId = RequireTenantId();

        var category = await _context.ServiceCategories.FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tenantId);
        if (category == null)
            throw new ArgumentException("Kategorie nicht gefunden");

        // Check if another category with same name exists
        var existing = await _context.ServiceCategories
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Name.ToLower() == dto.Name.ToLower() && c.Id != id);

        if (existing != null)
            throw new ArgumentException("Eine andere Kategorie mit diesem Namen existiert bereits");

        category.Name = dto.Name;
        category.Description = dto.Description;
        category.DisplayOrder = dto.DisplayOrder;
        category.IsActive = dto.IsActive;
        category.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        _logger.LogInformation("Updated category: {CategoryName}", category.Name);

        return await GetCategoryByIdAdminAsync(category.Id)
            ?? throw new InvalidOperationException("Failed to retrieve updated category");
    }

    public async Task<bool> DeleteCategoryAsync(Guid id)
    {
        var tenantId = RequireTenantId();

        var category = await _context.ServiceCategories
            .Include(c => c.Services)
            .FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tenantId);

        if (category == null)
            throw new ArgumentException("Kategorie nicht gefunden");

        // Check if category has any services
        if (category.Services.Any())
            throw new InvalidOperationException("Kann Kategorie mit bestehenden Services nicht löschen");

        _context.ServiceCategories.Remove(category);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Deleted category: {CategoryName}", category.Name);
        return true;
    }

    // ── EMPLOYEE ASSIGNMENT METHODS ────────────────────────────────

    public async Task<List<EmployeeForAssignmentDto>> GetEmployeesForAssignmentAsync()
    {
        var tenantId = RequireTenantId();

        var employees = await _context.Employees
            .Include(e => e.ServiceEmployees)
            .Where(e => e.TenantId == tenantId && e.IsActive)
            .OrderBy(e => e.Name)
            .Select(e => new EmployeeForAssignmentDto(
                e.Id,
                e.Name,
                e.Role,
                e.Specialty,
                e.ServiceEmployees.Count
            ))
            .ToListAsync();

        return employees;
    }

    public async Task<List<AdminServiceDto>> GetServicesByEmployeeAsync(Guid employeeId)
    {
        var tenantId = RequireTenantId();

        var services = await _context.Services
            .Include(s => s.Category)
            .Include(s => s.ServiceEmployees)
                .ThenInclude(se => se.Employee)
            .Where(s => s.TenantId == tenantId && s.IsActive && s.ServiceEmployees.Any(se => se.EmployeeId == employeeId && se.Employee.TenantId == tenantId))
            .OrderBy(s => s.Category.DisplayOrder)
            .ThenBy(s => s.DisplayOrder)
            .Select(s => new AdminServiceDto(
                s.Id,
                s.Name,
                s.Description,
                s.DurationMinutes,
                s.BufferTimeMinutes,
                s.Price,
                s.DisplayOrder,
                s.CategoryId,
                s.Category.Name,
                s.Currency,
                s.ServiceEmployees.Select(se => new EmployeeBasicDto(
                    se.Employee.Id,
                    se.Employee.Name,
                    se.Employee.Role,
                    se.Employee.Specialty
                )).ToList(),
                s.IsActive,
                s.LocationId,
                s.Location != null ? s.Location.Name : null
            ))
            .ToListAsync();

        return services;
    }

    public async Task<bool> BulkAssignServicesToEmployeeAsync(Guid employeeId, List<Guid> serviceIds)
    {
        var tenantId = RequireTenantId();

        var employee = await _context.Employees.FirstOrDefaultAsync(e => e.Id == employeeId && e.TenantId == tenantId);
        if (employee == null)
            throw new ArgumentException("Mitarbeiter nicht gefunden");

        // Remove existing assignments for this employee
        var existingAssignments = await _context.ServiceEmployees
            .Where(se => se.EmployeeId == employeeId && se.Employee.TenantId == tenantId && se.Service.TenantId == tenantId)
            .ToListAsync();

        _context.ServiceEmployees.RemoveRange(existingAssignments);

        // Add new assignments
        foreach (var serviceId in serviceIds.Distinct())
        {
            var service = await _context.Services.FirstOrDefaultAsync(s => s.Id == serviceId && s.TenantId == tenantId);
            if (service != null)
            {
                _context.ServiceEmployees.Add(new ServiceEmployee
                {
                    ServiceId = serviceId,
                    EmployeeId = employeeId,
                    CreatedAt = DateTime.UtcNow
                });
            }
        }

        await _context.SaveChangesAsync();

        _logger.LogInformation("Bulk assigned {Count} services to employee {EmployeeId}",
            serviceIds.Count, employeeId);

        return true;
    }
}
