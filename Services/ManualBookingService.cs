using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.DTOs;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Services;

public class ManualBookingService
{
    private readonly GentleBookDbContext _context;
    private readonly ILogger<ManualBookingService> _logger;
    private readonly EmailService _emailService;
    private readonly ITenantContext _tenantContext;

    public ManualBookingService(
        GentleBookDbContext context,
        ILogger<ManualBookingService> logger,
        EmailService emailService,
        ITenantContext tenantContext)
    {
        _context = context;
        _logger = logger;
        _emailService = emailService;
        _tenantContext = tenantContext;
    }

    public async Task<ManualBookingResponseDto> CreateManualBookingAsync(CreateManualBookingDto dto)
    {
        var tenantId = _tenantContext.TenantId
            ?? throw new InvalidOperationException("TenantId fehlt");

        // Validate required fields
        if (string.IsNullOrWhiteSpace(dto.FirstName))
            throw new ArgumentException("Vorname ist erforderlich");

        if (string.IsNullOrWhiteSpace(dto.LastName))
            throw new ArgumentException("Nachname ist erforderlich");

        // Validate service
        var service = await _context.Services
            .FirstOrDefaultAsync(s => s.Id == dto.ServiceId && s.IsActive);

        if (service == null)
            throw new ArgumentException("Service nicht gefunden oder inaktiv");

        // Parse date and time
        if (!DateOnly.TryParse(dto.BookingDate, out var bookingDate))
            throw new ArgumentException("Ungültiges Datumsformat");

        if (!TimeOnly.TryParse(dto.StartTime, out var startTime))
            throw new ArgumentException("Ungültiges Zeitformat");

        var endTime = startTime.AddMinutes(service.DurationMinutes);

        // Plan limit: bookings per calendar month (same enforcement as public bookings)
        var subscription = await _context.Subscriptions.FirstOrDefaultAsync(s => s.TenantId == tenantId);
        var planLimits = GentleBook.Api.Configuration.PlanLimits.Get(subscription?.Plan ?? SubscriptionPlan.Trial);
        if (!GentleBook.Api.Configuration.PlanLimits.IsUnlimited(planLimits.MaxBookingsPerMonth))
        {
            var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var monthCount = await _context.Bookings.CountAsync(b =>
                b.TenantId == tenantId &&
                b.CreatedAt >= monthStart &&
                b.Status != BookingStatus.Cancelled);
            if (monthCount >= planLimits.MaxBookingsPerMonth)
                throw new InvalidOperationException(
                    $"Ihr Plan erlaubt maximal {planLimits.MaxBookingsPerMonth} Buchungen pro Monat. Bitte upgraden Sie Ihren Plan.");
        }

        // Check if slot is available
        var isAvailable = await IsSlotAvailableAsync(tenantId, bookingDate, startTime, endTime, dto.EmployeeId);
        if (!isAvailable)
            throw new InvalidOperationException("Dieser Zeitslot ist nicht verfügbar");

        // Resolve optional employee
        Employee? employee = null;
        if (dto.EmployeeId.HasValue)
        {
            employee = await _context.Employees
                .FirstOrDefaultAsync(e => e.Id == dto.EmployeeId.Value && e.IsActive);
        }

        // Find or create customer (with employee assignment)
        var customer = await FindOrCreateCustomer(dto, dto.EmployeeId);

        // Create booking
        var booking = new Booking
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CustomerId = customer.Id,
            ServiceId = service.Id,
            EmployeeId = employee?.Id,
            BookingDate = bookingDate,
            StartTime = startTime,
            EndTime = endTime,
            Status = BookingStatus.Confirmed, // Auto-confirm for manual bookings
            CustomerNotes = dto.CustomerNotes,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.Bookings.Add(booking);

        // Update customer stats
        customer.TotalBookings++;
        customer.LastVisit = DateTime.UtcNow;
        customer.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Manual booking created: {BookingNumber} for customer {CustomerId} - {FirstName} {LastName} assigned to employee {EmployeeId}",
            Booking.GenerateBookingNumber(bookingDate, booking.Id),
            customer.Id,
            customer.FirstName,
            customer.LastName,
            customer.EmployeeId
        );

        // Try to send confirmation email if email is provided
        bool emailSent = false;
        if (!string.IsNullOrEmpty(customer.Email))
        {
            try
            {
                await _emailService.SendBookingConfirmationAsync(booking.Id);
                emailSent = true;

                _context.EmailLogs.Add(new EmailLog
                {
                    Id = Guid.NewGuid(),
                    TenantId = booking.TenantId,
                    BookingId = booking.Id,
                    RecipientEmail = customer.Email,
                    EmailType = EmailType.Confirmation,
                    SentAt = DateTime.UtcNow,
                    Status = EmailStatus.Sent
                });
                await _context.SaveChangesAsync();

                _logger.LogInformation("Confirmation email sent to {Email}", customer.Email);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not send confirmation email to {Email}", customer.Email);
            }
        }

        return new ManualBookingResponseDto(
            booking.Id,
            Booking.GenerateBookingNumber(bookingDate, booking.Id),
            booking.Status.ToString(),
            emailSent,
            new BookingDetailsDto(
                service.Id,
                service.Name,
                bookingDate.ToString("yyyy-MM-dd"),
                startTime.ToString("HH:mm"),
                endTime.ToString("HH:mm"),
                service.Price,
                service.Currency
            ),
            new CustomerBasicDto(
                customer.FirstName,
                customer.LastName,
                customer.Email,
                customer.EmployeeId
            ),
            employee != null
                ? new EmployeeDto(employee.Id, employee.Name, employee.Role, employee.Specialty)
                : null
        );
    }

    // In ManualBookingService.cs
    public async Task<EmailConflictCheckDto> CheckEmailConflictAsync(string email, string firstName, string lastName, Guid? employeeId)
    {
        var tenantId = _tenantContext.TenantId;
        if (string.IsNullOrWhiteSpace(email) || !tenantId.HasValue)
        {
            return new EmailConflictCheckDto { HasConflict = false };
        }

        var normalizedEmail = email.Trim().ToLower();
        var existingCustomer = await _context.Customers
            .FirstOrDefaultAsync(c => c.TenantId == tenantId.Value
                && c.Email != null
                && c.Email.ToLower() == normalizedEmail
                && (!employeeId.HasValue || c.EmployeeId == employeeId.Value));

        if (existingCustomer == null)
        {
            return new EmailConflictCheckDto { HasConflict = false };
        }

        // Check if names match
        string existingFullName = $"{existingCustomer.FirstName} {existingCustomer.LastName}".Trim();
        string newFullName = $"{firstName} {lastName}".Trim();

        bool namesMatch = string.Equals(existingFullName, newFullName, StringComparison.OrdinalIgnoreCase);

        if (!namesMatch)
        {
            return new EmailConflictCheckDto
            {
                HasConflict = true,
                ExistingName = existingFullName,
                ExistingEmail = existingCustomer.Email
            };
        }

        return new EmailConflictCheckDto { HasConflict = false };
    }

    public async Task<ManualBookingResponseDto?> GetManualBookingByIdAsync(Guid id)
    {
        var booking = await _context.Bookings
            .Include(b => b.Customer)
            .Include(b => b.Service)
            .Include(b => b.Employee)
            .FirstOrDefaultAsync(b => b.Id == id);

        if (booking == null) return null;

        return new ManualBookingResponseDto(
            booking.Id,
            Booking.GenerateBookingNumber(booking.BookingDate, booking.Id),
            booking.Status.ToString(),
            booking.ConfirmationSentAt.HasValue,
            new BookingDetailsDto(
                booking.Service.Id,
                booking.Service.Name,
                booking.BookingDate.ToString("yyyy-MM-dd"),
                booking.StartTime.ToString("HH:mm"),
                booking.EndTime.ToString("HH:mm"),
                booking.Service.Price,
                booking.Service.Currency
            ),
            new CustomerBasicDto(
                booking.Customer.FirstName,
                booking.Customer.LastName,
                booking.Customer.Email,
                booking.Customer.EmployeeId
            ),
            booking.Employee != null
                ? new EmployeeDto(booking.Employee.Id, booking.Employee.Name, booking.Employee.Role, booking.Employee.Specialty)
                : null
        );
    }

    private async Task<Customer> FindOrCreateCustomer(CreateManualBookingDto dto, Guid? employeeId)
    {
        var tenantId = _tenantContext.TenantId
            ?? throw new InvalidOperationException("TenantId fehlt");

        // Normalize: treat empty/whitespace as null so unique indexes work correctly
        var email = string.IsNullOrWhiteSpace(dto.Email) ? null : dto.Email.Trim();
        var phone = string.IsNullOrWhiteSpace(dto.Phone) ? null : dto.Phone.Trim();
        var firstName = dto.FirstName.Trim();
        var lastName = dto.LastName.Trim();

        Customer? customer = null;

        // 1. Find by email (tenant-wide for admins, employee-scoped for employees)
        if (email != null)
        {
            customer = await _context.Customers
                .FirstOrDefaultAsync(c => c.TenantId == tenantId
                    && c.Email != null
                    && c.Email.ToLower() == email.ToLower()
                    && (!employeeId.HasValue || c.EmployeeId == employeeId.Value));

            if (customer != null)
                _logger.LogInformation("Found existing customer by email: {Email} for employee {EmployeeId}", email, employeeId);
        }

        // 2. Find by phone (tenant-wide for admins, employee-scoped for employees)
        if (customer == null && phone != null)
        {
            customer = await _context.Customers
                .FirstOrDefaultAsync(c => c.TenantId == tenantId
                    && c.Phone == phone
                    && (!employeeId.HasValue || c.EmployeeId == employeeId.Value));

            if (customer != null)
                _logger.LogInformation("Found existing customer by phone: {Phone} for employee {EmployeeId}", phone, employeeId);
        }

        // 3. Find by name (tenant-wide for admins, employee-scoped for employees)
        if (customer == null)
        {
            customer = await _context.Customers
                .FirstOrDefaultAsync(c =>
                    c.TenantId == tenantId &&
                    c.FirstName.ToLower() == firstName.ToLower() &&
                    c.LastName.ToLower() == lastName.ToLower() &&
                    (!employeeId.HasValue || c.EmployeeId == employeeId.Value));

            if (customer != null)
                _logger.LogInformation("Found existing customer by name: {FirstName} {LastName} for employee {EmployeeId}",
                    firstName, lastName, employeeId);
        }

        if (customer != null)
        {
            // Update name
            customer.FirstName = firstName;
            customer.LastName = lastName;
            customer.UpdatedAt = DateTime.UtcNow;

            // Update email only if provided and not taken by another customer of this employee
            if (email != null && customer.Email != email)
            {
                var emailTaken = await _context.Customers
                    .AnyAsync(c => c.TenantId == tenantId
                        && c.Email == email
                        && (!employeeId.HasValue || c.EmployeeId == employeeId.Value)
                        && c.Id != customer.Id);

                if (!emailTaken)
                    customer.Email = email;
            }

            // Update phone only if provided and different
            if (phone != null && customer.Phone != phone)
            {
                var phoneTaken = await _context.Customers
                    .AnyAsync(c => c.TenantId == tenantId
                        && c.Phone == phone
                        && (!employeeId.HasValue || c.EmployeeId == employeeId.Value)
                        && c.Id != customer.Id);

                if (!phoneTaken)
                    customer.Phone = phone;
            }

            // Ensure employee is assigned
            if (!customer.EmployeeId.HasValue && employeeId.HasValue)
                customer.EmployeeId = employeeId;

            _logger.LogInformation("Updated existing customer: {CustomerId} for employee {EmployeeId}",
                customer.Id, customer.EmployeeId);
        }
        else
        {
            // Create new customer and assign to employee
            customer = new Customer
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                FirstName = firstName,
                LastName = lastName,
                Email = email,   // null if not provided
                Phone = phone,   // null if not provided
                EmployeeId = employeeId, // Assign to the employee creating the booking
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                TotalBookings = 0,
                NoShowCount = 0,
            };

            _context.Customers.Add(customer);
            _logger.LogInformation("Created new customer: {FirstName} {LastName} for employee {EmployeeId}",
                firstName, lastName, employeeId);
        }

        return customer;
    }

    private async Task<bool> IsSlotAvailableAsync(
        Guid tenantId,
        DateOnly date,
        TimeOnly startTime,
        TimeOnly endTime,
        Guid? employeeId = null)
    {
        // CASE 1: Specific employee requested
        if (employeeId.HasValue)
        {
            // Check for conflicting bookings for this specific employee
            var hasBookingConflict = await _context.Bookings
                .AnyAsync(b =>
                    b.TenantId == tenantId &&
                    b.BookingDate == date &&
                    b.EmployeeId == employeeId.Value &&
                    b.Status != BookingStatus.Cancelled &&
                    b.StartTime < endTime &&
                    b.EndTime > startTime);

            if (hasBookingConflict)
                return false;

            // Check for blocked slots for this specific employee
            var hasBlockedConflict = await _context.BlockedTimeSlots
                .AnyAsync(bs =>
                    bs.TenantId == tenantId &&
                    bs.BlockDate == date &&
                    bs.EmployeeId == employeeId.Value &&
                    bs.StartTime < endTime &&
                    bs.EndTime > startTime);

            return !hasBlockedConflict;
        }

        // CASE 2: No employee specified - check if ANY active employee is available
        else
        {
            var activeEmployees = await _context.Employees
                .Where(e => e.TenantId == tenantId && e.IsActive)
                .Select(e => e.Id)
                .ToListAsync();

            foreach (var empId in activeEmployees)
            {
                // Check bookings for this employee
                var hasBookingConflict = await _context.Bookings
                    .AnyAsync(b =>
                        b.TenantId == tenantId &&
                        b.BookingDate == date &&
                        b.EmployeeId == empId &&
                        b.Status != BookingStatus.Cancelled &&
                        b.StartTime < endTime &&
                        b.EndTime > startTime);

                if (hasBookingConflict)
                    continue;

                // Check blocked slots for this employee
                var hasBlockedConflict = await _context.BlockedTimeSlots
                    .AnyAsync(bs =>
                        bs.TenantId == tenantId &&
                        bs.BlockDate == date &&
                        bs.EmployeeId == empId &&
                        bs.StartTime < endTime &&
                        bs.EndTime > startTime);

                if (!hasBlockedConflict)
                    return true; // Found at least one available employee
            }

            return false; // No employee available
        }
    }
}
