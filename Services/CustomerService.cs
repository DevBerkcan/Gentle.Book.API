using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.DTOs;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Services;

public class CustomerService
{
    private readonly GentleBookDbContext _context;
    private readonly ILogger<CustomerService> _logger;
    private readonly ITenantContext _tenantContext;

    public CustomerService(GentleBookDbContext context, ILogger<CustomerService> logger, ITenantContext tenantContext)
    {
        _context = context;
        _logger = logger;
        _tenantContext = tenantContext;
    }

    // ── GET ALL (with employee filtering) ───────────────────────────────────
    public async Task<PagedResponseDto<CustomerListItemDto>> GetCustomersAsync(
        Guid? employeeId,
        string? searchTerm,
        int page = 1,
        int pageSize = 20)
    {
        var query = _context.Customers
            .Where(c => c.TenantId == _tenantContext.TenantId)
            .AsQueryable();

        // Filter by employee if specified
        if (employeeId.HasValue)
            query = query.Where(c => c.EmployeeId == employeeId.Value);

        // Apply search
        if (!string.IsNullOrEmpty(searchTerm))
        {
            var search = searchTerm.ToLower();
            query = query.Where(c =>
                c.FirstName.ToLower().Contains(search) ||
                c.LastName.ToLower().Contains(search) ||
                (c.Email != null && c.Email.ToLower().Contains(search)) ||
                (c.Phone != null && c.Phone.Contains(search))
            );
        }

        var totalCount = await query.CountAsync();

        var customers = await query
            .OrderByDescending(c => c.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new CustomerListItemDto(
                c.Id,
                c.FirstName + " " + c.LastName,
                c.Email,
                c.Phone,
                c.TotalBookings,
                c.LastVisit,
                c.CreatedAt
            ))
            .ToListAsync();

        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);

        return new PagedResponseDto<CustomerListItemDto>(
            customers,
            totalCount,
            page,
            pageSize,
            totalPages
        );
    }

    // ── GET BY ID (with employee ownership check) ───────────────────────────
    public async Task<CustomerDetailDto?> GetCustomerByIdAsync(Guid id, Guid? employeeId, bool isAdmin = false)
    {
        var query = _context.Customers
            .Include(c => c.Bookings)
                .ThenInclude(b => b.Service)
            .Include(c => c.Bookings)
                .ThenInclude(b => b.Employee)
            .AsQueryable();

        // Check ownership if not admin
        if (!isAdmin && employeeId.HasValue)
            query = query.Where(c => c.EmployeeId == employeeId.Value);

        var customer = await query.FirstOrDefaultAsync(c => c.Id == id);

        if (customer == null)
            return null;

        // Calculate NoShowCount from bookings
        var noShowCount = customer.Bookings.Count(b => b.Status == BookingStatus.NoShow);

        // Optional: Update the customer entity if the count doesn't match
        if (customer.NoShowCount != noShowCount)
        {
            customer.NoShowCount = noShowCount;
            await _context.SaveChangesAsync();
        }

        var recentBookings = customer.Bookings
            .OrderByDescending(b => b.BookingDate)
            .ThenByDescending(b => b.StartTime)
            .Take(10)
            .Select(b => new CustomerBookingItemDto(
                b.Id,
                Booking.GenerateBookingNumber(b.BookingDate, b.Id),
                b.Service?.Name ?? "Unknown",
                b.BookingDate.ToString("yyyy-MM-dd"),
                b.StartTime.ToString("HH:mm"),
                b.EndTime.ToString("HH:mm"),
                b.Status.ToString(),
                b.Service?.Price ?? 0,
                b.Service?.Currency ?? "CHF"
            ))
            .ToList();

        return new CustomerDetailDto(
            customer.Id,
            customer.FirstName,
            customer.LastName,
            customer.Email,
            customer.Phone,
            customer.CreatedAt,
            customer.UpdatedAt,
            customer.LastVisit,
            customer.TotalBookings,
            customer.NoShowCount,
            customer.Notes,
            customer.FullName,
            customer.EmployeeId,
            recentBookings
        );
    }

    // ── CREATE ───────────────────────────────────────────────────────────────
    public async Task<CustomerResponseDto> CreateCustomerAsync(CreateCustomerRequestDto dto, Guid employeeId)
    {
        // Check for existing customer with same email/phone for this employee
        if (!string.IsNullOrEmpty(dto.Email))
        {
            var existingEmail = await _context.Customers
                .FirstOrDefaultAsync(c => c.EmployeeId == employeeId && c.Email == dto.Email);
            if (existingEmail != null)
                throw new InvalidOperationException("Ein Kunde mit dieser E-Mail existiert bereits");
        }

        if (!string.IsNullOrEmpty(dto.Phone))
        {
            var existingPhone = await _context.Customers
                .FirstOrDefaultAsync(c => c.EmployeeId == employeeId && c.Phone == dto.Phone);
            if (existingPhone != null)
                throw new InvalidOperationException("Ein Kunde mit dieser Telefonnummer existiert bereits");
        }

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantContext.TenantId!.Value,
            FirstName = dto.FirstName?.Trim() ?? string.Empty,
            LastName = dto.LastName?.Trim() ?? string.Empty,
            Email = dto.Email?.Trim(),
            Phone = dto.Phone?.Trim(),
            Notes = dto.Notes?.Trim(),
            EmployeeId = employeeId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            TotalBookings = 0,
            NoShowCount = 0
        };

        _context.Customers.Add(customer);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Customer created: {CustomerId} for employee {EmployeeId}", customer.Id, employeeId);

        return ToDto(customer);
    }

    // ── UPDATE ───────────────────────────────────────────────────────────────
    public async Task<CustomerResponseDto> UpdateCustomerAsync(Guid id, UpdateCustomerRequestDto dto, Guid? employeeId, bool isAdmin = false)
    {
        var query = _context.Customers.AsQueryable();

        // Check ownership if not admin
        if (!isAdmin && employeeId.HasValue)
            query = query.Where(c => c.EmployeeId == employeeId.Value);

        var customer = await query.FirstOrDefaultAsync(c => c.Id == id);

        if (customer == null)
            throw new ArgumentException("Kunde nicht gefunden");

        // Check for duplicate email/phone within this employee's customers
        if (!string.IsNullOrEmpty(dto.Email) && dto.Email != customer.Email)
        {
            var existingEmail = await _context.Customers
                .FirstOrDefaultAsync(c => c.EmployeeId == customer.EmployeeId && c.Email == dto.Email && c.Id != id);
            if (existingEmail != null)
                throw new InvalidOperationException("Ein Kunde mit dieser E-Mail existiert bereits");
        }

        if (!string.IsNullOrEmpty(dto.Phone) && dto.Phone != customer.Phone)
        {
            var existingPhone = await _context.Customers
                .FirstOrDefaultAsync(c => c.EmployeeId == customer.EmployeeId && c.Phone == dto.Phone && c.Id != id);
            if (existingPhone != null)
                throw new InvalidOperationException("Ein Kunde mit dieser Telefonnummer existiert bereits");
        }

        customer.FirstName = dto.FirstName.Trim();
        customer.LastName = dto.LastName.Trim();
        customer.Email = dto.Email?.Trim();
        customer.Phone = dto.Phone?.Trim();
        customer.Notes = dto.Notes?.Trim();
        customer.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        _logger.LogInformation("Customer updated: {CustomerId}", customer.Id);

        return ToDto(customer);
    }

    // ── DELETE ───────────────────────────────────────────────────────────────
    public async Task DeleteCustomerAsync(Guid id, Guid? employeeId, bool isAdmin = false)
    {
        var query = _context.Customers
            .Include(c => c.Bookings)  // Include bookings
                .ThenInclude(b => b.EmailLogs)  // Include email logs for cascade
            .AsQueryable();

        // Check ownership if not admin
        if (!isAdmin && employeeId.HasValue)
            query = query.Where(c => c.EmployeeId == employeeId.Value);

        var customer = await query.FirstOrDefaultAsync(c => c.Id == id);

        if (customer == null)
            throw new ArgumentException("Kunde nicht gefunden");

        // Start a transaction to ensure all deletes succeed or fail together
        using var transaction = await _context.Database.BeginTransactionAsync();

        try
        {
            // Delete all bookings for this customer
            if (customer.Bookings != null && customer.Bookings.Any())
            {
                _logger.LogInformation("Deleting {Count} bookings for customer {CustomerId}",
                    customer.Bookings.Count, customer.Id);

                // Delete all email logs for these bookings
                foreach (var booking in customer.Bookings)
                {
                    if (booking.EmailLogs != null && booking.EmailLogs.Any())
                    {
                        _context.EmailLogs.RemoveRange(booking.EmailLogs);
                    }
                }

                // Delete all bookings
                _context.Bookings.RemoveRange(customer.Bookings);
            }

            // Delete the customer
            _context.Customers.Remove(customer);

            await _context.SaveChangesAsync();
            await transaction.CommitAsync();

            _logger.LogInformation(
                "Customer deleted: {CustomerId} with {BookingCount} bookings",
                customer.Id,
                customer.Bookings?.Count ?? 0);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _logger.LogError(ex, "Error deleting customer {CustomerId}", customer.Id);
            throw new InvalidOperationException("Fehler beim Löschen des Kunden und seiner Buchungen", ex);
        }
    }

    // ── SEARCH (for dropdown/autocomplete) ──────────────────────────────────
    public async Task<List<CustomerListItemDto>> SearchCustomersAsync(
        Guid? employeeId,
        string searchTerm,
        int limit = 10)
    {
        if (string.IsNullOrWhiteSpace(searchTerm) || !employeeId.HasValue)
            return new List<CustomerListItemDto>();

        var query = _context.Customers
            .Where(c => c.EmployeeId == employeeId.Value)
            .AsQueryable();

        var search = searchTerm.ToLower();
        query = query.Where(c =>
            c.FirstName.ToLower().Contains(search) ||
            c.LastName.ToLower().Contains(search) ||
            (c.Email != null && c.Email.ToLower().Contains(search)) ||
            (c.Phone != null && c.Phone.Contains(search))
        );

        return await query
            .OrderBy(c => c.FirstName)
            .Take(limit)
            .Select(c => new CustomerListItemDto(
                c.Id,
                c.FirstName + " " + c.LastName,
                c.Email,
                c.Phone,
                c.TotalBookings,
                c.LastVisit,
                c.CreatedAt
            ))
            .ToListAsync();
    }

    // ── Helper to convert to DTO ─────────────────────────────────────────────
    private static CustomerResponseDto ToDto(Customer c) => new(
        c.Id,
        c.FirstName,
        c.LastName,
        c.Email,
        c.Phone,
        c.CreatedAt,
        c.UpdatedAt,
        c.LastVisit,
        c.TotalBookings,
        c.NoShowCount,
        c.Notes,
        c.FullName,
        c.EmployeeId
    );
}