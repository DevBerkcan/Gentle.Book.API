using GentleBook.Api.Data;
using GentleBook.Api.DTOs;
using GentleBook.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Services;

public class AvailabilityService
{
    private readonly GentleBookDbContext _context;
    private readonly ILogger<AvailabilityService> _logger;
    private readonly ITenantContext _tenantContext;

    public AvailabilityService(GentleBookDbContext context, ILogger<AvailabilityService> logger, ITenantContext tenantContext)
    {
        _context = context;
        _logger = logger;
        _tenantContext = tenantContext;
    }

    // Mirrors ServiceService/EmployeeService: prefer the authenticated tenant (from the JWT),
    // fall back to a client-supplied slug for the unauthenticated public booking widget.
    // Every query below must filter by the resolved TenantId explicitly — this endpoint is
    // reachable without authentication, so the EF global query filter (which is a no-op when
    // no tenant is set) cannot be relied on here.
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

    /// <summary>
    /// Get available time slots for a specific service and date, optionally filtered by employee.
    /// When employeeId is provided, only that employee's availability is checked.
    /// When no employeeId is provided, checks if ANY employee is available.
    /// </summary>
    public async Task<AvailabilityResponseDto> GetAvailableTimeSlotsAsync(
        Guid serviceId,
        DateOnly date,
        Guid? employeeId = null,
        string? waitlistToken = null,
        string? tenantSlug = null)
    {
        var tenantId = await ResolveTenantIdAsync(tenantSlug);
        if (!tenantId.HasValue)
            throw new ArgumentException("Tenant not found", nameof(tenantSlug));

        // 1. Service abrufen
        var service = await _context.Services
            .FirstOrDefaultAsync(s => s.Id == serviceId && s.TenantId == tenantId.Value);
        if (service == null || !service.IsActive)
        {
            throw new ArgumentException("Service not found or inactive", nameof(serviceId));
        }

        // 2. Öffnungszeiten für den Wochentag abrufen
        var dayOfWeek = date.DayOfWeek;
        var businessHours = await _context.BusinessHours
            .FirstOrDefaultAsync(bh => bh.TenantId == tenantId.Value && bh.DayOfWeek == dayOfWeek);

        if (businessHours == null || !businessHours.IsOpen)
        {
            var noHoursMsg = businessHours == null
                ? "Für diesen Tag wurden noch keine Öffnungszeiten eingerichtet."
                : "Das Studio ist an diesem Wochentag geschlossen.";
            return new AvailabilityResponseDto(
                date.ToString("yyyy-MM-dd"),
                serviceId,
                service.DurationMinutes,
                new List<TimeSlotDto>(),
                noHoursMsg
            );
        }

        // 3. Buchungsintervall aus Settings holen
        var tenantSettings = await _context.TenantSettings.FirstOrDefaultAsync(s => s.TenantId == tenantId.Value);
        var intervalMinutes = tenantSettings?.BookingIntervalMinutes ?? 15;

        // 4. Alle Zeitslots generieren
        var timeSlots = GenerateTimeSlots(
            businessHours.OpenTime,
            businessHours.CloseTime,
            service.DurationMinutes,
            intervalMinutes,
            businessHours.BreakStartTime,
            businessHours.BreakEndTime
        );

        // 5. Verfügbarkeit basierend auf Mitarbeiter prüfen
        var availableSlots = await DetermineAvailableSlots(
            timeSlots,
            date,
            employeeId,
            service.DurationMinutes,
            tenantId.Value
        );
        availableSlots = await ApplyWaitlistReservationsAsync(
            availableSlots, serviceId, date, employeeId, waitlistToken);

        // Erkläre WARUM keine Zeiten frei sind, statt nur eine leere Liste zu liefern
        string? message = null;
        if (employeeId.HasValue && availableSlots.All(s => !s.IsAvailable))
        {
            message = await BuildEmployeeUnavailableMessageAsync(date, employeeId.Value, tenantId.Value);
        }

        return new AvailabilityResponseDto(
            date.ToString("yyyy-MM-dd"),
            serviceId,
            service.DurationMinutes,
            availableSlots,
            message
        );
    }

    private async Task<List<TimeSlotDto>> ApplyWaitlistReservationsAsync(
        List<TimeSlotDto> slots,
        Guid serviceId,
        DateOnly date,
        Guid? employeeId,
        string? waitlistToken)
    {
        var reservations = await _context.WaitlistEntries
            .AsNoTracking()
            .Where(w =>
                w.ServiceId == serviceId &&
                w.PreferredDate == date &&
                w.ReservedEmployeeId == employeeId &&
                w.Status == WaitlistStatus.Notified &&
                w.ReservationExpiresAt > DateTime.UtcNow)
            .Select(w => new
            {
                w.ReservationToken,
                w.ReservedStartTime,
                w.ReservedEndTime,
            })
            .ToListAsync();

        if (reservations.Count == 0)
            return slots;

        return slots.Select(slot =>
        {
            if (!TimeOnly.TryParse(slot.StartTime, out var start) ||
                !TimeOnly.TryParse(slot.EndTime, out var end))
                return slot;

            var reservation = reservations.FirstOrDefault(r =>
                r.ReservedStartTime == start && r.ReservedEndTime == end);
            var belongsToCaller = reservation == null ||
                string.Equals(reservation.ReservationToken, waitlistToken, StringComparison.Ordinal);

            return belongsToCaller ? slot : slot with { IsAvailable = false };
        }).ToList();
    }

    /// <summary>
    /// Builds a human-readable reason why an employee has no free slots on a date.
    /// </summary>
    private async Task<string?> BuildEmployeeUnavailableMessageAsync(DateOnly date, Guid employeeId, Guid tenantId)
    {
        var isOnVacation = await _context.EmployeeVacations
            .AnyAsync(v => v.TenantId == tenantId && v.EmployeeId == employeeId && v.StartDate <= date && v.EndDate >= date);
        if (isOnVacation)
            return "Der ausgewählte Mitarbeiter ist an diesem Tag abwesend (Urlaub/Abwesenheit).";

        var schedule = await _context.EmployeeSchedules
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.EmployeeId == employeeId && s.DayOfWeek == date.DayOfWeek);

        if (schedule != null && !schedule.IsWorkingDay)
            return "Der ausgewählte Mitarbeiter arbeitet laut Arbeitszeiten an diesem Wochentag nicht.";

        if (schedule != null)
            return $"Keine freien Zeiten: Der Mitarbeiter arbeitet an diesem Tag von {schedule.StartTime:HH\\:mm} bis {schedule.EndTime:HH\\:mm} Uhr — alle passenden Zeiten sind belegt, blockiert oder liegen außerhalb der Öffnungszeiten.";

        return "Alle Zeiten an diesem Tag sind bereits belegt oder blockiert.";
    }

    /// <summary>
    /// Get available time slots for all employees (for admin view)
    /// </summary>
    public async Task<Dictionary<Guid, List<TimeSlotDto>>> GetAllEmployeesAvailabilityAsync(
        DateOnly date,
        int serviceDuration,
        string? tenantSlug = null)
    {
        var tenantId = await ResolveTenantIdAsync(tenantSlug);
        if (!tenantId.HasValue)
            return new Dictionary<Guid, List<TimeSlotDto>>();

        var employees = await _context.Employees
            .Where(e => e.TenantId == tenantId.Value && e.IsActive)
            .ToListAsync();

        var result = new Dictionary<Guid, List<TimeSlotDto>>();

        foreach (var employee in employees)
        {
            var slots = await GetAvailableTimeSlotsForEmployeeAsync(date, serviceDuration, employee.Id, tenantId.Value);
            result[employee.Id] = slots;
        }

        return result;
    }

    /// <summary>
    /// Get available time slots for a specific employee
    /// </summary>
    public async Task<List<TimeSlotDto>> GetAvailableTimeSlotsForEmployeeAsync(
        DateOnly date,
        int serviceDuration,
        Guid employeeId,
        Guid tenantId)
    {
        var dayOfWeek = date.DayOfWeek;
        var businessHours = await _context.BusinessHours
            .FirstOrDefaultAsync(bh => bh.TenantId == tenantId && bh.DayOfWeek == dayOfWeek);

        if (businessHours == null || !businessHours.IsOpen)
            return new List<TimeSlotDto>();

        var tenantSettings = await _context.TenantSettings.FirstOrDefaultAsync(s => s.TenantId == tenantId);
        var intervalMinutes = tenantSettings?.BookingIntervalMinutes ?? 15;

        var timeSlots = GenerateTimeSlots(
            businessHours.OpenTime,
            businessHours.CloseTime,
            serviceDuration,
            intervalMinutes,
            businessHours.BreakStartTime,
            businessHours.BreakEndTime
        );

        return await DetermineAvailableSlotsForEmployee(
            timeSlots,
            date,
            employeeId,
            serviceDuration,
            tenantId
        );
    }

    /// <summary>
    /// Check if a specific time slot is available for a given employee
    /// </summary>
    public async Task<bool> IsTimeSlotAvailableForEmployeeAsync(
        DateOnly date,
        TimeOnly startTime,
        TimeOnly endTime,
        Guid employeeId,
        Guid tenantId)
    {
        // Check for any non-cancelled booking for this employee at this time.
        // EndTime is extended by the existing booking's service BufferTimeMinutes
        // to block the cleanup/preparation gap after each appointment.
        var existingBooking = await _context.Bookings
            .Include(b => b.Service)
            .AnyAsync(b => b.TenantId == tenantId &&
                          b.BookingDate == date &&
                          b.EmployeeId == employeeId &&
                          b.Status != BookingStatus.Cancelled &&
                          b.StartTime < endTime &&
                          b.EndTime.AddMinutes(b.Service.BufferTimeMinutes) > startTime);

        if (existingBooking)
            return false;

        // Check for blocked slots: employee-specific OR studio-wide (EmployeeId == null)
        var isBlocked = await _context.BlockedTimeSlots
            .AnyAsync(bs => bs.TenantId == tenantId &&
                           bs.BlockDate == date &&
                           (bs.EmployeeId == employeeId || bs.EmployeeId == null) &&
                           bs.StartTime < endTime &&
                           bs.EndTime > startTime);

        return !isBlocked;
    }

    /// <summary>
    /// Public entry point for callers (e.g. the controller) that only have a tenant slug,
    /// not an already-resolved TenantId. Resolves the tenant, then delegates.
    /// </summary>
    public async Task<bool> CheckSlotAvailabilityAsync(
        DateOnly date,
        TimeOnly startTime,
        TimeOnly endTime,
        Guid employeeId,
        string? tenantSlug = null)
    {
        var tenantId = await ResolveTenantIdAsync(tenantSlug);
        if (!tenantId.HasValue)
            return false;

        return await IsTimeSlotAvailableForEmployeeAsync(date, startTime, endTime, employeeId, tenantId.Value);
    }

    /// <summary>
    /// Legacy method for backward compatibility
    /// </summary>
    public async Task<bool> IsTimeSlotAvailableAsync(Guid serviceId, DateOnly date, TimeOnly startTime, string? tenantSlug = null)
    {
        var tenantId = await ResolveTenantIdAsync(tenantSlug);
        if (!tenantId.HasValue)
            return false;

        var service = await _context.Services.FirstOrDefaultAsync(s => s.Id == serviceId && s.TenantId == tenantId.Value);
        if (service == null || !service.IsActive)
            return false;

        var endTime = startTime.AddMinutes(service.DurationMinutes);

        // For backward compatibility, check if ANY employee is available
        var employees = await _context.Employees
            .Where(e => e.TenantId == tenantId.Value && e.IsActive)
            .Select(e => e.Id)
            .ToListAsync();

        foreach (var employeeId in employees)
        {
            var isAvailable = await IsTimeSlotAvailableForEmployeeAsync(date, startTime, endTime, employeeId, tenantId.Value);
            if (isAvailable)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Get all available employees for a given time slot
    /// </summary>
    public async Task<List<Guid>> GetAvailableEmployeesForTimeSlotAsync(
        DateOnly date,
        TimeOnly startTime,
        TimeOnly endTime,
        string? tenantSlug = null)
    {
        var tenantId = await ResolveTenantIdAsync(tenantSlug);
        if (!tenantId.HasValue)
            return new List<Guid>();

        var activeEmployees = await _context.Employees
            .Where(e => e.TenantId == tenantId.Value && e.IsActive)
            .Select(e => e.Id)
            .ToListAsync();

        var availableEmployees = new List<Guid>();

        foreach (var employeeId in activeEmployees)
        {
            var isAvailable = await IsTimeSlotAvailableForEmployeeAsync(date, startTime, endTime, employeeId, tenantId.Value);
            if (isAvailable)
                availableEmployees.Add(employeeId);
        }

        return availableEmployees;
    }

    // ── Private helper methods ──────────────────────────────────────────

    private List<(TimeOnly Start, TimeOnly End)> GenerateTimeSlots(
        TimeOnly openTime,
        TimeOnly closeTime,
        int serviceDuration,
        int intervalMinutes,
        TimeOnly? breakStart,
        TimeOnly? breakEnd)
    {
        var slots = new List<(TimeOnly Start, TimeOnly End)>();
        var currentTime = openTime;

        while (currentTime.AddMinutes(serviceDuration) <= closeTime)
        {
            var slotEnd = currentTime.AddMinutes(serviceDuration);

            // Prüfe ob Slot in Pausenzeit fällt
            if (breakStart.HasValue && breakEnd.HasValue)
            {
                var inBreak = currentTime < breakEnd.Value && slotEnd > breakStart.Value;
                if (inBreak)
                {
                    currentTime = currentTime.AddMinutes(intervalMinutes);
                    continue;
                }
            }

            slots.Add((currentTime, slotEnd));
            currentTime = currentTime.AddMinutes(intervalMinutes);
        }

        return slots;
    }

    private async Task<List<TimeSlotDto>> DetermineAvailableSlots(
        List<(TimeOnly Start, TimeOnly End)> timeSlots,
        DateOnly date,
        Guid? employeeId,
        int serviceDuration,
        Guid tenantId)
    {
        var availableSlots = new List<TimeSlotDto>();

        if (employeeId.HasValue)
        {
            // Check availability for specific employee
            availableSlots = await DetermineAvailableSlotsForEmployee(
                timeSlots, date, employeeId.Value, serviceDuration, tenantId);
        }
        else
        {
            // Check if ANY employee is available for each slot
            var activeEmployees = await _context.Employees
                .Where(e => e.TenantId == tenantId && e.IsActive)
                .Select(e => e.Id)
                .ToListAsync();

            foreach (var slot in timeSlots)
            {
                var isAvailable = false;
                foreach (var empId in activeEmployees)
                {
                    var available = await IsTimeSlotAvailableForEmployeeAsync(
                        date, slot.Start, slot.End, empId, tenantId);
                    if (available)
                    {
                        isAvailable = true;
                        break;
                    }
                }

                availableSlots.Add(new TimeSlotDto(
                    slot.Start.ToString("HH:mm"),
                    slot.End.ToString("HH:mm"),
                    isAvailable
                ));
            }
        }

        return availableSlots;
    }

    private async Task<List<TimeSlotDto>> DetermineAvailableSlotsForEmployee(
        List<(TimeOnly Start, TimeOnly End)> timeSlots,
        DateOnly date,
        Guid employeeId,
        int serviceDuration,
        Guid tenantId)
    {
        var availableSlots = new List<TimeSlotDto>();

        // Check if employee is on vacation this day
        var isOnVacation = await _context.EmployeeVacations
            .AnyAsync(v => v.TenantId == tenantId && v.EmployeeId == employeeId && v.StartDate <= date && v.EndDate >= date);
        if (isOnVacation)
        {
            return timeSlots.Select(slot => new TimeSlotDto(
                slot.Start.ToString("HH:mm"),
                slot.End.ToString("HH:mm"),
                false
            )).ToList();
        }

        // Check employee's personal schedule for this day
        var dayOfWeek = date.DayOfWeek;
        var employeeSchedule = await _context.EmployeeSchedules
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.EmployeeId == employeeId && s.DayOfWeek == dayOfWeek);

        // If schedule exists and employee is not working → all slots unavailable
        if (employeeSchedule != null && !employeeSchedule.IsWorkingDay)
        {
            return timeSlots.Select(slot => new TimeSlotDto(
                slot.Start.ToString("HH:mm"),
                slot.End.ToString("HH:mm"),
                false
            )).ToList();
        }

        // Get all bookings for this employee on this date
        var employeeBookings = await _context.Bookings
            .Where(b => b.TenantId == tenantId &&
                       b.BookingDate == date &&
                       b.EmployeeId == employeeId &&
                       b.Status != BookingStatus.Cancelled)
            .Select(b => new { b.StartTime, b.EndTime })
            .ToListAsync();

        // Get all blocked slots for this employee on this date,
        // including studio-wide blocks (EmployeeId == null) created by the admin
        var employeeBlocked = await _context.BlockedTimeSlots
            .Where(bs => bs.TenantId == tenantId &&
                        bs.BlockDate == date &&
                        (bs.EmployeeId == employeeId || bs.EmployeeId == null))
            .Select(bs => new { bs.StartTime, bs.EndTime })
            .ToListAsync();

        foreach (var slot in timeSlots)
        {
            // Check if slot falls within employee's personal working hours
            var outsideSchedule = employeeSchedule != null && (
                slot.Start < employeeSchedule.StartTime ||
                slot.End > employeeSchedule.EndTime
            );

            // Check if slot is booked
            var isBooked = employeeBookings.Any(b =>
                b.StartTime < slot.End && b.EndTime > slot.Start);

            // Check if slot is blocked
            var isBlocked = employeeBlocked.Any(b =>
                b.StartTime < slot.End && b.EndTime > slot.Start);

            availableSlots.Add(new TimeSlotDto(
                slot.Start.ToString("HH:mm"),
                slot.End.ToString("HH:mm"),
                !outsideSchedule && !isBooked && !isBlocked
            ));
        }

        return availableSlots;
    }
}
