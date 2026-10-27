using System.Data;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.DTOs;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Services;

public class BookingService
{
    private readonly GentleBookDbContext _context;
    private readonly ILogger<BookingService> _logger;
    private readonly EmailService _emailService;
    private readonly IBackgroundJobClient _jobs;
    private readonly VoucherService _voucherService;

    public BookingService(
        GentleBookDbContext context,
        ILogger<BookingService> logger,
        EmailService emailService,
        IBackgroundJobClient jobs,
        VoucherService voucherService)
    {
        _context = context;
        _jobs = jobs;
        _logger = logger;
        _emailService = emailService;
        _voucherService = voucherService;
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
        var tenantId = service.TenantId;

        // Reject past date/time in the tenant's own timezone — checking only the calendar date
        // (as before) let a customer book a same-day slot that had already started/passed,
        // since nothing compared the requested time-of-day against "now".
        var tenantTimeZoneId = await _context.TenantSettings
            .Where(s => s.TenantId == tenantId)
            .Select(s => s.TimeZone)
            .FirstOrDefaultAsync() ?? "Europe/Berlin";
        TimeZoneInfo tenantTz;
        try { tenantTz = TimeZoneInfo.FindSystemTimeZoneById(tenantTimeZoneId); }
        catch { tenantTz = TimeZoneInfo.Utc; }
        var tenantLocalNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tenantTz);

        if (bookingDate.ToDateTime(startTime) < tenantLocalNow)
            throw new InvalidOperationException("Termine in der Vergangenheit können nicht gebucht werden.");

        // 2b. Tenant must be active and have a valid trial/subscription.
        // Anonymous requests bypass TenantMiddleware, so this is enforced here:
        // expired or deactivated tenants must not accept new bookings.
        var tenantActive = await _context.Tenants.AnyAsync(t => t.Id == tenantId && t.IsActive);
        if (!tenantActive)
            throw new InvalidOperationException("Dieses Buchungssystem ist derzeit nicht verfügbar.");

        var subscription = await _context.Subscriptions.FirstOrDefaultAsync(s => s.TenantId == tenantId);
        if (subscription == null || !subscription.IsAccessAllowed)
            throw new InvalidOperationException("Online-Buchungen sind für dieses Studio derzeit nicht möglich. Bitte kontaktieren Sie das Studio direkt.");

        // 2c. Plan limit: bookings per calendar month (Trial 100, Starter 200, Pro/Business unbegrenzt)
        var planLimits = GentleBook.Api.Configuration.PlanLimits.Get(subscription.Plan);
        if (!GentleBook.Api.Configuration.PlanLimits.IsUnlimited(planLimits.MaxBookingsPerMonth))
        {
            var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var monthCount = await _context.Bookings.CountAsync(b =>
                b.TenantId == tenantId &&
                b.CreatedAt >= monthStart &&
                b.Status != BookingStatus.Cancelled);
            if (monthCount >= planLimits.MaxBookingsPerMonth)
                throw new InvalidOperationException("Das monatliche Buchungskontingent dieses Studios ist ausgeschöpft. Bitte kontaktieren Sie das Studio direkt.");
        }

        // 3. Check slot availability inside a serializable transaction to prevent race conditions.
        // Serializable (not RepeatableRead) is required to also block phantom inserts from a
        // concurrent request for the same slot — RepeatableRead only locks rows already read.
        await using var tx = await _context.Database
            .BeginTransactionAsync(IsolationLevel.Serializable);

        var activeReservation = await _context.WaitlistEntries.FirstOrDefaultAsync(w =>
            w.TenantId == tenantId &&
            w.ServiceId == dto.ServiceId &&
            w.PreferredDate == bookingDate &&
            w.Status == WaitlistStatus.Notified &&
            w.ReservationExpiresAt > DateTime.UtcNow &&
            w.ReservedStartTime == startTime &&
            w.ReservedEndTime == endTime &&
            w.ReservedEmployeeId == dto.EmployeeId);

        if (activeReservation != null &&
            !string.Equals(activeReservation.ReservationToken, dto.WaitlistToken, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Dieser Termin ist momentan für einen Wartelistenkunden reserviert.");
        }

        var isSlotAvailable = await IsSlotAvailableAsync(
            tenantId, bookingDate, startTime, endTime, dto.EmployeeId);

        if (!isSlotAvailable)
            throw new InvalidOperationException("Dieser Zeitslot ist bereits gebucht");

        var bookingDateTime = bookingDate.ToDateTime(startTime);

        // 4. Check max advance booking days
        var maxAdvanceDays = await GetSettingValueAsync(tenantId, "MAX_ADVANCE_BOOKING_DAYS", 60);
        if (bookingDate > DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(maxAdvanceDays)))
            throw new InvalidOperationException(
                $"Termine können maximal {maxAdvanceDays} Tage im Voraus gebucht werden");

        // 5-6. Find or create customer (lookup by email OR phone)
        var customer = await FindOrCreateCustomerForPublicBookingAsync(tenantId, dto.Customer, dto.EmployeeId);

        // 7. Resolve employee (optional) — must belong to the same tenant as the service
        Employee? employee = null;
        if (dto.EmployeeId.HasValue)
        {
            employee = await _context.Employees
                .FirstOrDefaultAsync(e => e.Id == dto.EmployeeId && e.TenantId == tenantId && e.IsActive);
        }

        // 8. Create booking
        var booking = new Booking
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CustomerId = customer.Id,
            ServiceId = service.Id,
            EmployeeId = employee?.Id,
            // Derived from the service, not trusted from the client DTO — a service's
            // location is the source of truth (set once by the admin), never client-supplied.
            LocationId = service.LocationId,
            BookingDate = bookingDate,
            StartTime = startTime,
            EndTime = endTime,
            Status = BookingStatus.Confirmed,
            CustomerNotes = dto.CustomerNotes,
            // Snapshot of what was actually typed on THIS booking's form — the matched customer
            // (by email/phone) may have a different real name (a partner booking under the same
            // email, or anyone who knows a stranger's email). See Booking.ContactFirstName.
            ContactFirstName = dto.Customer.FirstName,
            ContactLastName = dto.Customer.LastName,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        _context.Bookings.Add(booking);

        if (activeReservation != null)
        {
            activeReservation.Status = WaitlistStatus.Booked;
            activeReservation.BookingId = booking.Id;
            activeReservation.BookedAt = DateTime.UtcNow;
        }

        customer.TotalBookings++;
        // LastVisit means "the customer was actually here," not "has a reservation" — a booking
        // for a future date must not overwrite it before the appointment has happened. Only a
        // same-day/already-past booking (e.g. a walk-in logged immediately) counts right away;
        // a genuinely future booking advances LastVisit later, when it's actually completed
        // (BookingCompletionService / AdminService.UpdateBookingStatusAsync).
        if (bookingDateTime <= DateTime.UtcNow)
            customer.LastVisit = DateTime.SpecifyKind(bookingDateTime, DateTimeKind.Utc);
        customer.UpdatedAt = DateTime.UtcNow;

        // Same trigger point as the manual/staff booking flow (ManualBookingService) — a
        // customer is rewarded regardless of whether they booked themselves or staff did.
        await _voucherService.MaybeIssueAutoRewardAsync(tenantId, customer);

        await _context.SaveChangesAsync();
        await tx.CommitAsync();

        _logger.LogInformation(
            "Booking created: {BookingId} for customer {Email} on {Date} at {Time}, employee: {EmployeeId}",
            booking.Id, customer.Email, bookingDate, startTime, employee?.Id);

        // 9. Send confirmation email — EmailService handles the EmailLog internally. Backgrounded
        // via Hangfire (two full SMTP round trips — customer email + internal admin
        // notification — made this request itself take 10+ seconds otherwise); the booking
        // itself is already committed above regardless of whether/when this job runs.
        if (!string.IsNullOrEmpty(customer.Email))
        {
            _jobs.Enqueue<EmailService>(s => s.SendBookingConfirmationAsync(booking.Id));
        }

        return ToDto(booking, customer, service, employee);
    }

    // Internal: resolves the Customer for a public, [AllowAnonymous] booking by email/phone,
    // creating one if no match exists. Split out from CreateBookingAsync (which immediately
    // opens a SERIALIZABLE transaction EF Core's InMemory provider doesn't support) so this —
    // the part that matters for data integrity — is directly unit-testable.
    public async Task<Customer> FindOrCreateCustomerForPublicBookingAsync(
        Guid tenantId, CustomerInfoDto customerDto, Guid? employeeId)
    {
        var email = string.IsNullOrWhiteSpace(customerDto.Email) ? null : customerDto.Email.Trim();
        var phone = string.IsNullOrWhiteSpace(customerDto.Phone) ? null : customerDto.Phone.Trim();

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
                FirstName = customerDto.FirstName,
                LastName = customerDto.LastName,
                Email = email,
                Phone = phone,
                EmployeeId = employeeId
            };
            _context.Customers.Add(customer);
        }
        else
        {
            // Anyone can hit this endpoint with any email/phone they know — letting that
            // overwrite the matched customer's real name would retroactively rename them
            // across all their past bookings. Only fill in contact details that were
            // genuinely missing before; never touch the name of an existing match.
            if (email != null && customer.Email == null) customer.Email = email;
            if (phone != null && customer.Phone == null) customer.Phone = phone;
            if (email != null || phone != null) customer.UpdatedAt = DateTime.UtcNow;
        }

        return customer;
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

    /// <summary>
    /// Tenant owning a given booking, for callers that already have an authenticated
    /// tenant context and need to verify they're not viewing another tenant's booking
    /// via a guessed/leaked id (GetBooking is AllowAnonymous for the public confirmation
    /// page, so the global tenant query filter is bypassed there by design).
    /// </summary>
    public async Task<Guid?> GetBookingTenantIdAsync(Guid id)
    {
        return await _context.Bookings
            .Where(b => b.Id == id)
            .Select(b => (Guid?)b.TenantId)
            .FirstOrDefaultAsync();
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
    /// Loyalty point balance per tenant this email is a customer of (Agency tenants only —
    /// LoyaltyService never awards points elsewhere, so other tenants would just show 0).
    /// Same cross-tenant-by-email lookup as GetBookingsByEmailAsync above.
    /// </summary>
    public async Task<List<(string TenantName, int Points)>> GetLoyaltyBalancesByEmailAsync(string email)
    {
        var customers = await _context.Customers
            .Include(c => c.Tenant)
            .Where(c => c.Email != null && c.Email.ToLower() == email.ToLower() && c.LoyaltyPoints > 0)
            .ToListAsync();

        return customers.Select(c => (c.Tenant.Name, c.LoyaltyPoints)).ToList();
    }

    /// <summary>
    /// Get all bookings.
    /// If employeeId is provided, only that employee's bookings are returned.
    /// If null, all bookings are returned (admin view).
    /// </summary>
    public async Task<List<BookingResponseDto>> GetAllBookingsAsync(Guid? employeeId = null, DateOnly? fromDate = null, DateOnly? toDate = null, Guid? locationId = null)
    {
        var query = _context.Bookings
            .Include(b => b.Customer)
            .Include(b => b.Service)
            .Include(b => b.Employee)
            .AsQueryable();

        if (employeeId.HasValue)
            query = query.Where(b => b.EmployeeId == employeeId.Value);

        if (fromDate.HasValue)
            query = query.Where(b => b.BookingDate >= fromDate.Value);
        if (toDate.HasValue)
            query = query.Where(b => b.BookingDate <= toDate.Value);
        if (locationId.HasValue)
            query = query.Where(b => b.LocationId == locationId.Value);

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

        // Vergangene Termine können vom Kunden nicht mehr storniert werden
        // (Admins nutzen den separaten Statuswechsel unter /api/admin/bookings).
        var bookingDateTime = booking.BookingDate.ToDateTime(booking.StartTime);
        var hoursUntil = (bookingDateTime - DateTime.UtcNow).TotalHours;
        if (hoursUntil <= 0)
            throw new InvalidOperationException("Dieser Termin liegt in der Vergangenheit und kann nicht mehr storniert werden.");

        // Stornierungsfrist prüfen (falls konfiguriert). TenantSettings has no global query
        // filter at all (unlike Booking/Customer/etc.), so an unfiltered FirstOrDefaultAsync()
        // here silently returned whichever tenant's settings happened to be first in the table
        // — every tenant's cancellation deadline was being enforced using tenant #1's setting.
        var settings = await _context.TenantSettings.FirstOrDefaultAsync(s => s.TenantId == booking.TenantId);
        if (settings?.CancellationHoursNotice > 0 && hoursUntil < settings.CancellationHoursNotice)
            throw new InvalidOperationException(
                $"Stornierung ist nur bis {settings.CancellationHoursNotice} Stunden vor dem Termin möglich.");

        booking.Status = BookingStatus.Cancelled;
        booking.CancelledAt = DateTime.UtcNow;
        booking.CancellationReason = dto.Reason;

        if (!string.IsNullOrWhiteSpace(booking.RedeemedVoucherCode))
            await _voucherService.RefundVoucherForBookingAsync(booking);

        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Booking cancelled: {BookingId}, Reason: {Reason}", booking.Id, dto.Reason);

        // Backgrounded via Hangfire (two full SMTP round trips — see CreateBookingAsync's
        // comment above); emailSent here now means "queued for sending", not "confirmed
        // delivered" — EmailLog (written by the job itself) is the actual source of truth for
        // delivery status.
        var emailSent = dto.NotifyCustomer;
        if (dto.NotifyCustomer)
        {
            _jobs.Enqueue<EmailService>(s => s.SendCancellationConfirmationByIdAsync(booking.Id, false));
        }

        // Offer the freed slot to the oldest matching waitlist entry.
        _jobs.Enqueue<WaitlistService>(service => service.NotifyNextForFreedSlotAsync(
            booking.TenantId,
            booking.ServiceId,
            booking.EmployeeId,
            booking.BookingDate,
            booking.StartTime,
            booking.EndTime));

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
            _jobs.Enqueue<EmailService>(s => s.SendConfirmationReceiptByIdAsync(booking.Id));
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

        // Backgrounded via Hangfire — see CreateBookingAsync's comment above.
        _jobs.Enqueue<EmailService>(s => s.SendConfirmationReceiptByIdAsync(booking.Id));

        return ToDto(booking, true);
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
            // Check for conflicting bookings for this specific employee.
            // EndTime is extended by the existing booking's BufferTimeMinutes so the
            // cleanup gap is enforced at booking time (consistent with the slot display).
            var hasBookingConflict = await _context.Bookings
                .Include(b => b.Service)
                .AnyAsync(b =>
                    b.TenantId == tenantId &&
                    b.BookingDate == date &&
                    b.EmployeeId == employeeId.Value &&
                    b.Status != BookingStatus.Cancelled &&
                    b.StartTime < endTime &&
                    b.EndTime.AddMinutes(b.Service.BufferTimeMinutes) > startTime);

            if (hasBookingConflict)
                return false;

            // Check for blocked slots: employee-specific OR studio-wide (EmployeeId == null)
            var hasBlockedConflict = await _context.BlockedTimeSlots
                .AnyAsync(bs =>
                    bs.TenantId == tenantId &&
                    bs.BlockDate == date &&
                    (bs.EmployeeId == employeeId.Value || bs.EmployeeId == null) &&
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
                // Check bookings for this employee (incl. buffer time after each booking)
                var hasBookingConflict = await _context.Bookings
                    .Include(b => b.Service)
                    .AnyAsync(b =>
                        b.TenantId == tenantId &&
                        b.BookingDate == date &&
                        b.EmployeeId == empId &&
                        b.Status != BookingStatus.Cancelled &&
                        b.StartTime < endTime &&
                        b.EndTime.AddMinutes(b.Service.BufferTimeMinutes) > startTime);

                if (hasBookingConflict)
                    continue;

                // Check blocked slots: employee-specific OR studio-wide (EmployeeId == null)
                var hasBlockedConflict = await _context.BlockedTimeSlots
                    .AnyAsync(bs =>
                        bs.TenantId == tenantId &&
                        bs.BlockDate == date &&
                        (bs.EmployeeId == empId || bs.EmployeeId == null) &&
                        bs.StartTime < endTime &&
                        bs.EndTime > startTime);

                if (!hasBlockedConflict)
                    return true; // Found at least one available employee
            }

            return false; // No employee available
        }
    }

    private async Task<int> GetSettingValueAsync(Guid tenantId, string key, int defaultValue)
    {
        // Same missing-tenant-filter issue as the cancellation-notice check above — this was
        // silently reading whichever tenant's settings happened to be first in the table for
        // MAX_ADVANCE_BOOKING_DAYS/BOOKING_INTERVAL_MINUTES too.
        var tenantSettings = await _context.TenantSettings.FirstOrDefaultAsync(s => s.TenantId == tenantId);
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
            // Prefer the name as actually typed on this booking (see Booking.ContactFirstName)
            // over the matched customer's stored name, which can legitimately differ — falls
            // back to the customer's name for bookings created before this field existed.
            new CustomerToBookingDto(
                b.ContactFirstName ?? customer.FirstName,
                b.ContactLastName ?? customer.LastName,
                customer.Email),
            employee == null
                ? null
                : new EmployeeDto(employee.Id, employee.Name, employee.Role, employee.Specialty)
        );
}
