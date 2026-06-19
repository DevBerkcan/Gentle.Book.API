using System.Data;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.DTOs;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Services;

public class BookingService
{
    private readonly GentleBookDbContext _context;
    private readonly ILogger<BookingService> _logger;
    private readonly EmailService _emailService;

    public BookingService(
        GentleBookDbContext context,
        ILogger<BookingService> logger,
        EmailService emailService)
    {
        _context = context;
        _logger = logger;
        _emailService = emailService;
    }

    // ── CREATE ────────────────────────────────────────────────────

    public async Task<BookingResponseDto> CreateBookingAsync(CreateBookingDto dto)
    {
        // 1. Validate service
        var service = await _context.Services.FindAsync(dto.ServiceId);
        if (service == null || !service.IsActive)
            throw new ArgumentException("Service nicht gefunden oder inaktiv");

        // 2. Parse date and time
        if (!DateOnly.TryParse(dto.BookingDate, out var bookingDate))
            throw new ArgumentException("Ungültiges Datumsformat");

        if (!TimeOnly.TryParse(dto.StartTime, out var startTime))
            throw new ArgumentException("Ungültiges Zeitformat");

        var endTime = startTime.AddMinutes(service.DurationMinutes);

        if (bookingDate < DateOnly.FromDateTime(DateTime.UtcNow.Date))
            throw new InvalidOperationException("Termine in der Vergangenheit können nicht gebucht werden.");

        var tenantId = service.TenantId;

        // 3. Check slot availability inside a serializable transaction to prevent race conditions
        await using var tx = await _context.Database
            .BeginTransactionAsync(IsolationLevel.RepeatableRead);

        var isSlotAvailable = await IsSlotAvailableAsync(
            tenantId, bookingDate, startTime, endTime, dto.EmployeeId);

        if (!isSlotAvailable)
            throw new InvalidOperationException("Dieser Zeitslot ist bereits gebucht");

        var bookingDateTime = bookingDate.ToDateTime(startTime);

        // 4. Check max advance booking days
        var maxAdvanceDays = await GetSettingValueAsync("MAX_ADVANCE_BOOKING_DAYS", 60);
        if (bookingDate > DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(maxAdvanceDays)))
            throw new InvalidOperationException(
                $"Termine können maximal {maxAdvanceDays} Tage im Voraus gebucht werden");

        // 5. Normalize empty strings → null
        var email = string.IsNullOrWhiteSpace(dto.Customer.Email)
            ? null : dto.Customer.Email.Trim();
        var phone = string.IsNullOrWhiteSpace(dto.Customer.Phone)
            ? null : dto.Customer.Phone.Trim();

        // 6. Find or create customer (lookup by email OR phone)
        Customer? customer = null;

        if (email != null)
            customer = await _context.Customers
                .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Email != null && c.Email == email);

        if (customer == null && phone != null)
            customer = await _context.Customers
                .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Phone != null && c.Phone == phone);

        if (customer == null)
        {
            customer = new Customer
            {
                TenantId = tenantId,
                FirstName = dto.Customer.FirstName,
                LastName = dto.Customer.LastName,
                Email = email,
                Phone = phone,
                EmployeeId = dto.EmployeeId
            };
            _context.Customers.Add(customer);
        }
        else
        {
            customer.FirstName = dto.Customer.FirstName;
            customer.LastName = dto.Customer.LastName;
            if (email != null) customer.Email = email;
            if (phone != null) customer.Phone = phone;
            customer.UpdatedAt = DateTime.UtcNow;
        }

        // 7. Resolve employee (optional)
        Employee? employee = null;
        if (dto.EmployeeId.HasValue)
        {
            employee = await _context.Employees
                .FirstOrDefaultAsync(e => e.Id == dto.EmployeeId && e.IsActive);
        }

        // 8. Create booking
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
            Status = BookingStatus.Confirmed,
            CustomerNotes = dto.CustomerNotes,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        _context.Bookings.Add(booking);

        customer.TotalBookings++;
        customer.LastVisit = DateTime.SpecifyKind(bookingDateTime, DateTimeKind.Utc);
        customer.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        await tx.CommitAsync();

        _logger.LogInformation(
            "Booking created: {BookingId} for customer {Email} on {Date} at {Time}, employee: {EmployeeId}",
            booking.Id, customer.Email, bookingDate, startTime, employee?.Id);

        // 9. Send confirmation email
        if (!string.IsNullOrEmpty(customer.Email))
        {
            try
            {
                await _emailService.SendBookingConfirmationAsync(booking.Id);
                booking.ConfirmationSentAt = DateTime.UtcNow;

                _context.EmailLogs.Add(new EmailLog
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    BookingId = booking.Id,
                    RecipientEmail = customer.Email,
                    EmailType = EmailType.Confirmation,
                    SentAt = DateTime.UtcNow,
                    Status = EmailStatus.Sent,
                });
                await _context.SaveChangesAsync();
                _logger.LogInformation("Confirmation email sent to {Email}", customer.Email);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send confirmation email to {Email}", customer.Email);

                _context.EmailLogs.Add(new EmailLog
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    BookingId = booking.Id,
                    RecipientEmail = customer.Email,
                    EmailType = EmailType.Confirmation,
                    SentAt = null,
                    Status = EmailStatus.Failed,
                    ErrorMessage = ex.Message,
                });
                await _context.SaveChangesAsync();
            }
        }

        return ToDto(booking, customer, service, employee);
    }

    // ── READ ──────────────────────────────────────────────────────

    public async Task<BookingResponseDto?> GetBookingByIdAsync(Guid id)
    {
        var booking = await _context.Bookings
            .Include(b => b.Customer)
            .Include(b => b.Service)
            .Include(b => b.Employee)
            .FirstOrDefaultAsync(b => b.Id == id);

        return booking == null ? null : ToDto(booking);
    }

    public async Task<List<BookingResponseDto>> GetBookingsByEmailAsync(string email)
    {
        var bookings = await _context.Bookings
            .Include(b => b.Customer)
            .Include(b => b.Service)
            .Include(b => b.Employee)
            .Where(b => b.Customer.Email != null &&
                        b.Customer.Email.ToLower() == email.ToLower())
            .OrderByDescending(b => b.BookingDate)
            .ThenByDescending(b => b.StartTime)
            .ToListAsync();

        return bookings.Select(ToDto).ToList();
    }

    /// <summary>
    /// Get all bookings.
    /// If employeeId is provided, only that employee's bookings are returned.
    /// If null, all bookings are returned (admin view).
    /// </summary>
    public async Task<List<BookingResponseDto>> GetAllBookingsAsync(Guid? employeeId = null)
    {
        var query = _context.Bookings
            .Include(b => b.Customer)
            .Include(b => b.Service)
            .Include(b => b.Employee)
            .AsQueryable();

        if (employeeId.HasValue)
            query = query.Where(b => b.EmployeeId == employeeId.Value);

        var bookings = await query
            .OrderByDescending(b => b.BookingDate)
            .ThenByDescending(b => b.StartTime)
            .ToListAsync();

        return bookings.Select(ToDto).ToList();
    }

    // ── CANCEL ────────────────────────────────────────────────────

    public async Task<CancelBookingResponseDto> CancelBookingAsync(Guid id, CancelBookingDto dto)
    {
        var booking = await _context.Bookings
            .Include(b => b.Customer)
            .Include(b => b.Service)
            .FirstOrDefaultAsync(b => b.Id == id);

        if (booking == null)
            throw new ArgumentException("Buchung nicht gefunden");

        if (booking.Status == BookingStatus.Cancelled)
            throw new InvalidOperationException("Buchung ist bereits storniert");

        if (booking.Status == BookingStatus.Completed)
            throw new InvalidOperationException("Abgeschlossene Buchungen können nicht storniert werden");

        // Stornierungsfrist prüfen (falls konfiguriert)
        var settings = await _context.TenantSettings.FirstOrDefaultAsync();
        if (settings?.CancellationHoursNotice > 0)
        {
            var bookingDateTime = booking.BookingDate.ToDateTime(booking.StartTime);
            var hoursUntil = (bookingDateTime - DateTime.UtcNow).TotalHours;
            if (hoursUntil < settings.CancellationHoursNotice && hoursUntil > 0)
                throw new InvalidOperationException(
                    $"Stornierung ist nur bis {settings.CancellationHoursNotice} Stunden vor dem Termin möglich.");
        }

        booking.Status = BookingStatus.Cancelled;
        booking.CancelledAt = DateTime.UtcNow;
        booking.CancellationReason = dto.Reason;

        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Booking cancelled: {BookingId}, Reason: {Reason}", booking.Id, dto.Reason);

        bool emailSent = false;
        if (dto.NotifyCustomer)
        {
            try
            {
                await _emailService.SendCancellationConfirmationAsync(
                    booking, booking.Customer, booking.Service);

                _context.EmailLogs.Add(new EmailLog
                {
                    TenantId = booking.TenantId,
                    BookingId = booking.Id,
                    RecipientEmail = booking.Customer.Email,
                    EmailType = EmailType.Cancellation,
                    SentAt = DateTime.UtcNow,
                    Status = EmailStatus.Sent,
                });
                await _context.SaveChangesAsync();
                emailSent = true;

                _logger.LogInformation("Cancellation email sent to {Email}", booking.Customer.Email);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to send cancellation email to {Email}", booking.Customer.Email);

                _context.EmailLogs.Add(new EmailLog
                {
                    TenantId = booking.TenantId,
                    BookingId = booking.Id,
                    RecipientEmail = booking.Customer.Email,
                    EmailType = EmailType.Cancellation,
                    SentAt = DateTime.UtcNow,
                    Status = EmailStatus.Failed,
                    ErrorMessage = ex.Message,
                });
                await _context.SaveChangesAsync();
            }
        }

        return new CancelBookingResponseDto(true, "Termin erfolgreich storniert", emailSent);
    }

    // ── CONFIRM ───────────────────────────────────────────────────

    public async Task<BookingResponseDto> ConfirmBookingAsync(Guid bookingId)
    {
        var booking = await _context.Bookings
            .Include(b => b.Customer)
            .Include(b => b.Service)
            .Include(b => b.Employee)
            .FirstOrDefaultAsync(b => b.Id == bookingId);

        if (booking == null)
            throw new ArgumentException("Buchung nicht gefunden");

        // Idempotent: already Confirmed → just resend receipt, no error
        if (booking.Status == BookingStatus.Confirmed)
        {
            try { await _emailService.SendConfirmationReceiptAsync(booking, booking.Customer, booking.Service); }
            catch (Exception ex) { _logger.LogError(ex, "Failed to resend receipt for already-confirmed booking {BookingId}", booking.Id); }
            return ToDto(booking, true);
        }

        if (booking.Status != BookingStatus.Pending)
            throw new InvalidOperationException(
                $"Buchung kann nicht bestätigt werden. Aktueller Status: {booking.Status}");

        var hoursSinceCreation = (DateTime.UtcNow - booking.CreatedAt).TotalHours;
        if (hoursSinceCreation > 24)
            throw new InvalidOperationException(
                "Bestätigungsfrist (24 Stunden) abgelaufen. Bitte erstellen Sie eine neue Buchung.");

        if (booking.BookingDate < DateOnly.FromDateTime(DateTime.UtcNow))
            throw new InvalidOperationException("Vergangene Buchungen können nicht bestätigt werden.");

        booking.Status = BookingStatus.Confirmed;
        booking.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Booking confirmed: {BookingId} for customer {Email}",
            booking.Id, booking.Customer.Email);

        bool emailSent = false;
        try
        {
            await _emailService.SendConfirmationReceiptAsync(
                booking, booking.Customer, booking.Service);

            _context.EmailLogs.Add(new EmailLog
            {
                TenantId = booking.TenantId,
                BookingId = booking.Id,
                RecipientEmail = booking.Customer.Email,
                EmailType = EmailType.Confirmation,
                SentAt = DateTime.UtcNow,
                Status = EmailStatus.Sent,
            });
            await _context.SaveChangesAsync();
            emailSent = true;

            _logger.LogInformation("Confirmation receipt sent to {Email}", booking.Customer.Email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to send confirmation receipt to {Email}", booking.Customer.Email);

            _context.EmailLogs.Add(new EmailLog
            {
                TenantId = booking.TenantId,
                BookingId = booking.Id,
                RecipientEmail = booking.Customer.Email,
                EmailType = EmailType.Confirmation,
                SentAt = DateTime.UtcNow,
                Status = EmailStatus.Failed,
                ErrorMessage = ex.Message,
            });
            await _context.SaveChangesAsync();
        }

        return ToDto(booking, emailSent);
    }

    // ── Private helpers ───────────────────────────────────────────

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

    private async Task<int> GetSettingValueAsync(string key, int defaultValue)
    {
        var tenantSettings = await _context.TenantSettings.FirstOrDefaultAsync();
        if (tenantSettings == null) return defaultValue;
        return key switch
        {
            "MAX_ADVANCE_BOOKING_DAYS" => tenantSettings.MaxAdvanceBookingDays,
            "BOOKING_INTERVAL_MINUTES" => tenantSettings.BookingIntervalMinutes,
            _ => defaultValue
        };
    }

    // ── DTO mapping ───────────────────────────────────────────────

    private static BookingResponseDto ToDto(Booking b) =>
        ToDto(b, b.Customer, b.Service, b.Employee, b.ConfirmationSentAt.HasValue);

    private static BookingResponseDto ToDto(Booking b, bool confirmationSent) =>
        ToDto(b, b.Customer, b.Service, b.Employee, confirmationSent);

    private static BookingResponseDto ToDto(
        Booking b,
        Customer customer,
        Service service,
        Employee? employee,
        bool confirmationSent = false) =>
        new(
            b.Id,
            Booking.GenerateBookingNumber(b.BookingDate, b.Id),
            b.Status.ToString(),
            confirmationSent,
            new BookingDetailsDto(
                service.Id,
                service.Name,
                b.BookingDate.ToString("yyyy-MM-dd"),
                b.StartTime.ToString("HH:mm"),
                b.EndTime.ToString("HH:mm"),
                service.Price,
                service.Currency
            ),
            new CustomerToBookingDto(customer.FirstName, customer.LastName, customer.Email),
            employee == null
                ? null
                : new EmployeeDto(employee.Id, employee.Name, employee.Role, employee.Specialty)
        );
}
