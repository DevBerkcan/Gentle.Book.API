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
    private readonly EmailService _emailService;

    public CustomerService(GentleBookDbContext context, ILogger<CustomerService> logger, ITenantContext tenantContext, EmailService emailService)
    {
        _context = context;
        _logger = logger;
        _tenantContext = tenantContext;
        _emailService = emailService;
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
    public async Task<CustomerResponseDto> CreateCustomerAsync(CreateCustomerRequestDto dto, Guid? employeeId)
    {
        var tenantId = _tenantContext.TenantId
            ?? throw new InvalidOperationException("TenantId fehlt im Kontext");

        // Duplicate check — scope by employee if present, otherwise tenant-wide
        if (!string.IsNullOrEmpty(dto.Email))
        {
            var existingEmail = await _context.Customers
                .Where(c => c.TenantId == tenantId && c.Email == dto.Email)
                .Where(c => employeeId == null || c.EmployeeId == employeeId)
                .AnyAsync();
            if (existingEmail)
                throw new InvalidOperationException("Ein Kunde mit dieser E-Mail existiert bereits");
        }

        if (!string.IsNullOrEmpty(dto.Phone))
        {
            var existingPhone = await _context.Customers
                .Where(c => c.TenantId == tenantId && c.Phone == dto.Phone)
                .Where(c => employeeId == null || c.EmployeeId == employeeId)
                .AnyAsync();
            if (existingPhone)
                throw new InvalidOperationException("Ein Kunde mit dieser Telefonnummer existiert bereits");
        }

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
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

        if (!string.IsNullOrEmpty(customer.Email))
        {
            customer.EmailVerificationToken = Guid.NewGuid().ToString("N");
            customer.EmailVerificationTokenExpiry = DateTime.UtcNow.AddDays(7);
            await _context.SaveChangesAsync();
            _ = _emailService.SendWelcomeEmailAsync(customer, customer.TenantId);
        }

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

    // ── DELETE (GDPR Art. 17: anonymize, don't hard-delete) ─────────────────
    // Previously hard-deleted the Customer row AND every Booking — correct for the PII, wrong
    // for the business: booking history/revenue statistics are the salon's own legitimate
    // business records and must survive a customer's erasure request (LEGAL_AUDIT_2026-09-06.md:
    // "Kundenlöschung löscht statt zu anonymisieren"). Now only the identifying fields are
    // cleared; the Customer row and all Bookings (needed for revenue/stats FKs) stay.
    public async Task DeleteCustomerAsync(Guid id, Guid? employeeId, bool isAdmin = false)
    {
        var query = _context.Customers
            .Include(c => c.Bookings)
            .AsQueryable();

        // Check ownership if not admin
        if (!isAdmin && employeeId.HasValue)
            query = query.Where(c => c.EmployeeId == employeeId.Value);

        var customer = await query.FirstOrDefaultAsync(c => c.Id == id);

        if (customer == null)
            throw new ArgumentException("Kunde nicht gefunden");

        customer.FirstName = "Gelöschter";
        customer.LastName = "Kunde";
        customer.Email = null;
        customer.Phone = null;
        customer.Notes = null;
        customer.EmailVerificationToken = null;
        customer.EmailVerificationTokenExpiry = null;
        customer.IsEmailVerified = false;
        customer.UpdatedAt = DateTime.UtcNow;

        foreach (var booking in customer.Bookings)
            booking.CustomerNotes = null;

        await _context.SaveChangesAsync();

        _logger.LogInformation("Customer anonymized (GDPR Art. 17 erasure): {CustomerId}", customer.Id);
    }

    // ── EXPORT (GDPR Art. 15) ────────────────────────────────────────────────
    // A full, portable copy of everything stored about this customer — their own profile plus
    // their complete booking history (GetCustomerByIdAsync only returns the 10 most recent
    // bookings for the admin-UI detail view, which is not a complete Art. 15 export).
    public async Task<object?> ExportCustomerDataAsync(Guid id, Guid? employeeId, bool isAdmin = false)
    {
        var query = _context.Customers
            .Include(c => c.Bookings).ThenInclude(b => b.Service)
            .Include(c => c.Bookings).ThenInclude(b => b.Employee)
            .Include(c => c.Bookings).ThenInclude(b => b.Location)
            .AsQueryable();

        if (!isAdmin && employeeId.HasValue)
            query = query.Where(c => c.EmployeeId == employeeId.Value);

        var customer = await query.FirstOrDefaultAsync(c => c.Id == id);
        if (customer == null)
            return null;

        return new
        {
            ExportedAt = DateTime.UtcNow,
            Profile = new
            {
                customer.Id,
                customer.FirstName,
                customer.LastName,
                customer.Email,
                customer.Phone,
                customer.Notes,
                customer.CreatedAt,
                customer.UpdatedAt,
                customer.LastVisit,
                customer.TotalBookings,
                customer.NoShowCount,
                customer.ConsentGivenAt,
                customer.LoyaltyPoints,
            },
            Bookings = customer.Bookings
                .OrderByDescending(b => b.BookingDate)
                .ThenByDescending(b => b.StartTime)
                .Select(b => new
                {
                    b.Id,
                    BookingNumber = Booking.GenerateBookingNumber(b.BookingDate, b.Id),
                    Service = b.Service?.Name,
                    Employee = b.Employee?.Name,
                    Location = b.Location?.Name,
                    b.BookingDate,
                    b.StartTime,
                    b.EndTime,
                    Status = b.Status.ToString(),
                    b.CustomerNotes,
                    b.AdminNotes,
                    b.CreatedAt,
                    b.CancelledAt,
                    b.CancellationReason,
                })
                .ToList(),
        };
    }

    // ── SEARCH (for dropdown/autocomplete) ──────────────────────────────────
    public async Task<List<CustomerListItemDto>> SearchCustomersAsync(
        Guid? employeeId,
        string searchTerm,
        int limit = 10)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
            return new List<CustomerListItemDto>();

        var tenantId = _tenantContext.TenantId;

        var query = _context.Customers
            .Where(c => tenantId == null || c.TenantId == tenantId)
            .AsQueryable();

        // Scope to employee only when caller is an employee (not TenantAdmin)
        if (employeeId.HasValue)
            query = query.Where(c => c.EmployeeId == employeeId.Value);

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