using GentleBook.Api.Data;
using GentleBook.Api.DTOs;
using GentleBook.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Services;

public class AvailabilityService
{
    private readonly GentleBookDbContext _context;
    private readonly ILogger<AvailabilityService> _logger;

    public AvailabilityService(GentleBookDbContext context, ILogger<AvailabilityService> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Get available time slots for a specific service and date, optionally filtered by employee.
    /// When employeeId is provided, only that employee's availability is checked.
    /// When no employeeId is provided, checks if ANY employee is available.
    /// </summary>
    public async Task<AvailabilityResponseDto> GetAvailableTimeSlotsAsync(
        Guid serviceId,
        DateOnly date,
        Guid? employeeId = null)
    {
        // 1. Service abrufen
        var service = await _context.Services.FindAsync(serviceId);
        if (service == null || !service.IsActive)
        {
            throw new ArgumentException("Service not found or inactive", nameof(serviceId));
        }

        // 2. Öffnungszeiten für den Wochentag abrufen
        var dayOfWeek = date.DayOfWeek;
        var businessHours = await _context.BusinessHours
            .FirstOrDefaultAsync(bh => bh.DayOfWeek == dayOfWeek);

        if (businessHours == null || !businessHours.IsOpen)
        {
            return new AvailabilityResponseDto(
                date.ToString("yyyy-MM-dd"),
                serviceId,
                service.DurationMinutes,
                new List<TimeSlotDto>()
            );
        }

        // 3. Buchungsintervall aus Settings holen
        var tenantSettings = await _context.TenantSettings.FirstOrDefaultAsync();
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
            service.DurationMinutes
        );

        return new AvailabilityResponseDto(
            date.ToString("yyyy-MM-dd"),
            serviceId,
            service.DurationMinutes,
            availableSlots
        );
    }

    /// <summary>
    /// Get available time slots for all employees (for admin view)
    /// </summary>
    public async Task<Dictionary<Guid, List<TimeSlotDto>>> GetAllEmployeesAvailabilityAsync(
        DateOnly date,
        int serviceDuration)
    {
        var employees = await _context.Employees
            .Where(e => e.IsActive)
            .ToListAsync();

        var result = new Dictionary<Guid, List<TimeSlotDto>>();

        foreach (var employee in employees)
        {
            var slots = await GetAvailableTimeSlotsForEmployeeAsync(date, serviceDuration, employee.Id);
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
        Guid employeeId)
    {
        var dayOfWeek = date.DayOfWeek;
        var businessHours = await _context.BusinessHours
            .FirstOrDefaultAsync(bh => bh.DayOfWeek == dayOfWeek);

        if (businessHours == null || !businessHours.IsOpen)
            return new List<TimeSlotDto>();

        var tenantSettings = await _context.TenantSettings.FirstOrDefaultAsync();
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
            serviceDuration
        );
    }

    /// <summary>
    /// Check if a specific time slot is available for a given employee
    /// </summary>
    public async Task<bool> IsTimeSlotAvailableForEmployeeAsync(
        DateOnly date,
        TimeOnly startTime,
        TimeOnly endTime,
        Guid employeeId)
    {
        // Check for any non-cancelled booking for this employee at this time
        var existingBooking = await _context.Bookings
            .AnyAsync(b => b.BookingDate == date &&
                          b.EmployeeId == employeeId &&
                          b.Status != BookingStatus.Cancelled &&
                          b.StartTime < endTime &&
                          b.EndTime > startTime);

        if (existingBooking)
            return false;

        // Check for blocked slots for this employee
        var isBlocked = await _context.BlockedTimeSlots
            .AnyAsync(bs => bs.BlockDate == date &&
                           bs.EmployeeId == employeeId &&
                           bs.StartTime < endTime &&
                           bs.EndTime > startTime);

        return !isBlocked;
    }

    /// <summary>
    /// Legacy method for backward compatibility
    /// </summary>
    public async Task<bool> IsTimeSlotAvailableAsync(Guid serviceId, DateOnly date, TimeOnly startTime)
    {
        var service = await _context.Services.FindAsync(serviceId);
        if (service == null || !service.IsActive)
            return false;

        var endTime = startTime.AddMinutes(service.DurationMinutes);

        // For backward compatibility, check if ANY employee is available
        var employees = await _context.Employees.Where(e => e.IsActive).Select(e => e.Id).ToListAsync();

        foreach (var employeeId in employees)
        {
            var isAvailable = await IsTimeSlotAvailableForEmployeeAsync(date, startTime, endTime, employeeId);
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
        TimeOnly endTime)
    {
        var activeEmployees = await _context.Employees
            .Where(e => e.IsActive)
            .Select(e => e.Id)
            .ToListAsync();

        var availableEmployees = new List<Guid>();

        foreach (var employeeId in activeEmployees)
        {
            var isAvailable = await IsTimeSlotAvailableForEmployeeAsync(date, startTime, endTime, employeeId);
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
        int serviceDuration)
    {
        var availableSlots = new List<TimeSlotDto>();

        if (employeeId.HasValue)
        {
            // Check availability for specific employee
            availableSlots = await DetermineAvailableSlotsForEmployee(
                timeSlots, date, employeeId.Value, serviceDuration);
        }
        else
        {
            // Check if ANY employee is available for each slot
            var activeEmployees = await _context.Employees
                .Where(e => e.IsActive)
                .Select(e => e.Id)
                .ToListAsync();

            foreach (var slot in timeSlots)
            {
                var isAvailable = false;
                foreach (var empId in activeEmployees)
                {
                    var available = await IsTimeSlotAvailableForEmployeeAsync(
                        date, slot.Start, slot.End, empId);
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
        int serviceDuration)
    {
        var availableSlots = new List<TimeSlotDto>();

        // Get all bookings for this employee on this date
        var employeeBookings = await _context.Bookings
            .Where(b => b.BookingDate == date &&
                       b.EmployeeId == employeeId &&
                       b.Status != BookingStatus.Cancelled)
            .Select(b => new { b.StartTime, b.EndTime })
            .ToListAsync();

        // Get all blocked slots for this employee on this date
        var employeeBlocked = await _context.BlockedTimeSlots
            .Where(bs => bs.BlockDate == date &&
                        bs.EmployeeId == employeeId)
            .Select(bs => new { bs.StartTime, bs.EndTime })
            .ToListAsync();

        foreach (var slot in timeSlots)
        {
            // Check if slot is booked
            var isBooked = employeeBookings.Any(b =>
                b.StartTime < slot.End && b.EndTime > slot.Start);

            // Check if slot is blocked
            var isBlocked = employeeBlocked.Any(b =>
                b.StartTime < slot.End && b.EndTime > slot.Start);

            availableSlots.Add(new TimeSlotDto(
                slot.Start.ToString("HH:mm"),
                slot.End.ToString("HH:mm"),
                !isBooked && !isBlocked
            ));
        }

        return availableSlots;
    }
}