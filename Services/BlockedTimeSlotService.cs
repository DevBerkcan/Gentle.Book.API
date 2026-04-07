using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.DTOs;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Services;

public class BlockedTimeSlotService
{
    private readonly GentleBookDbContext _context;
    private readonly ILogger<BlockedTimeSlotService> _logger;

    public BlockedTimeSlotService(
        GentleBookDbContext context,
        ILogger<BlockedTimeSlotService> logger)
    {
        _context = context;
        _logger = logger;
    }

    // ── READ ──────────────────────────────────────────────────────

    /// <summary>
    /// Get blocked time slots within a date range.
    /// If employeeId is provided, only that employee's slots are returned.
    /// If null, all slots are returned (admin view).
    /// </summary>
    public async Task<List<BlockedTimeSlotDto>> GetBlockedTimeSlotsAsync(
        DateOnly startDate,
        DateOnly endDate,
        Guid? employeeId = null)
    {
        var query = _context.BlockedTimeSlots
            .Where(b => b.BlockDate >= startDate && b.BlockDate <= endDate);

        if (employeeId.HasValue)
            query = query.Where(b => b.EmployeeId == employeeId.Value);

        var blockedSlots = await query
            .OrderBy(b => b.BlockDate)
            .ThenBy(b => b.StartTime)
            .Select(b => new BlockedTimeSlotDto(
                b.Id,
                b.BlockDate,
                b.StartTime,
                b.EndTime,
                b.Reason,
                b.CreatedAt,
                b.EmployeeId
            ))
            .ToListAsync();

        return blockedSlots;
    }

    /// <summary>
    /// Get a single blocked time slot by ID.
    /// Returns null if not found.
    /// The controller is responsible for ownership checks using the EmployeeId on the returned DTO.
    /// </summary>
    public async Task<BlockedTimeSlotDto?> GetBlockedTimeSlotByIdAsync(Guid id)
    {
        var blockedSlot = await _context.BlockedTimeSlots
            .Where(b => b.Id == id)
            .Select(b => new BlockedTimeSlotDto(
                b.Id,
                b.BlockDate,
                b.StartTime,
                b.EndTime,
                b.Reason,
                b.CreatedAt,
                b.EmployeeId
            ))
            .FirstOrDefaultAsync();

        return blockedSlot;
    }

    // ── CREATE ────────────────────────────────────────────────────

    /// <summary>
    /// Create a blocked time slot for a single day.
    /// EmployeeId on the DTO is injected by the controller from JWT claims.
    /// Overlap checks are scoped to the same employee.
    /// </summary>
    public async Task<BlockedTimeSlotDto> CreateBlockedTimeSlotAsync(CreateBlockedTimeSlotDto dto)
    {
        if (!DateOnly.TryParse(dto.BlockDate, out var blockDate))
            throw new ArgumentException("Ungültiges Datumsformat. Verwende YYYY-MM-DD");

        if (!TimeOnly.TryParse(dto.StartTime, out var startTime))
            throw new ArgumentException("Ungültiges Zeitformat. Verwende HH:mm");

        if (!TimeOnly.TryParse(dto.EndTime, out var endTime))
            throw new ArgumentException("Ungültiges Zeitformat. Verwende HH:mm");

        if (startTime >= endTime)
            throw new ArgumentException("Startzeit muss vor Endzeit liegen");

        if (blockDate < DateOnly.FromDateTime(DateTime.UtcNow.Date))
            throw new ArgumentException("Datum kann nicht in der Vergangenheit liegen");

        // Overlap check — scoped to same employee (or global slots with no employee)
        var overlapQuery = _context.BlockedTimeSlots
            .Where(b =>
                b.BlockDate == blockDate &&
                b.StartTime < endTime &&
                b.EndTime > startTime);

        if (dto.EmployeeId.HasValue)
            overlapQuery = overlapQuery.Where(b =>
                b.EmployeeId == dto.EmployeeId || b.EmployeeId == null);

        if (await overlapQuery.AnyAsync())
            throw new InvalidOperationException(
                "Zeitslot überschneidet sich mit einem bestehenden blockierten Zeitraum");

        // Booking conflict check — scoped to same employee
        var bookingQuery = _context.Bookings
            .Where(b =>
                b.BookingDate == blockDate &&
                b.Status != BookingStatus.Cancelled &&
                b.StartTime < endTime &&
                b.EndTime > startTime);

        if (dto.EmployeeId.HasValue)
            bookingQuery = bookingQuery.Where(b => b.EmployeeId == dto.EmployeeId);

        if (await bookingQuery.AnyAsync())
            throw new InvalidOperationException(
                "Kann Zeitslot nicht blockieren – es existieren bereits Buchungen in diesem Zeitraum");

        var blockedSlot = new BlockedTimeSlot
        {
            Id = Guid.NewGuid(),
            BlockDate = blockDate,
            StartTime = startTime,
            EndTime = endTime,
            Reason = dto.Reason,
            EmployeeId = dto.EmployeeId,
            CreatedAt = DateTime.UtcNow,
        };

        _context.BlockedTimeSlots.Add(blockedSlot);
        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Blocked time slot created: {Id} for employee {EmployeeId} on {Date} {Start}-{End}",
            blockedSlot.Id, dto.EmployeeId, blockDate, startTime, endTime);

        return ToDto(blockedSlot);
    }

    /// <summary>
    /// Create blocked time slots across a date range.
    /// EmployeeId on the DTO is injected by the controller from JWT claims.
    /// </summary>
    public async Task<List<BlockedTimeSlotDto>> CreateBlockedDateRangeAsync(CreateBlockedDateRangeDto dto)
    {
        if (!DateOnly.TryParse(dto.FromDate, out var fromDate))
            throw new ArgumentException("Ungültiges Startdatum-Format. Verwende YYYY-MM-DD");

        if (!DateOnly.TryParse(dto.ToDate, out var toDate))
            throw new ArgumentException("Ungültiges Enddatum-Format. Verwende YYYY-MM-DD");

        if (!TimeOnly.TryParse(dto.StartTime, out var startTime))
            throw new ArgumentException("Ungültiges Startzeit-Format. Verwende HH:mm");

        if (!TimeOnly.TryParse(dto.EndTime, out var endTime))
            throw new ArgumentException("Ungültiges Endzeit-Format. Verwende HH:mm");

        if (fromDate > toDate)
            throw new ArgumentException("Startdatum muss vor Enddatum liegen");

        if (startTime >= endTime)
            throw new ArgumentException("Startzeit muss vor Endzeit liegen");

        if (fromDate < DateOnly.FromDateTime(DateTime.UtcNow.Date))
            throw new ArgumentException("Startdatum kann nicht in der Vergangenheit liegen");

        var createdSlots = new List<BlockedTimeSlotDto>();
        var currentDate = fromDate;

        while (currentDate <= toDate)
        {
            // Overlap check for this day — scoped to same employee
            var overlapQuery = _context.BlockedTimeSlots
                .Where(b =>
                    b.BlockDate == currentDate &&
                    b.StartTime < endTime &&
                    b.EndTime > startTime);

            if (dto.EmployeeId.HasValue)
                overlapQuery = overlapQuery.Where(b =>
                    b.EmployeeId == dto.EmployeeId || b.EmployeeId == null);

            if (await overlapQuery.AnyAsync())
                throw new InvalidOperationException(
                    $"Zeitslot am {currentDate:dd.MM.yyyy} überschneidet sich mit einem bestehenden blockierten Zeitraum");

            // Booking conflict check — scoped to same employee
            var bookingQuery = _context.Bookings
                .Where(b =>
                    b.BookingDate == currentDate &&
                    b.Status != BookingStatus.Cancelled &&
                    b.StartTime < endTime &&
                    b.EndTime > startTime);

            if (dto.EmployeeId.HasValue)
                bookingQuery = bookingQuery.Where(b => b.EmployeeId == dto.EmployeeId);

            if (await bookingQuery.AnyAsync())
                throw new InvalidOperationException(
                    $"Kann Zeitslot am {currentDate:dd.MM.yyyy} nicht blockieren – es existieren bereits Buchungen in diesem Zeitraum");

            var blockedSlot = new BlockedTimeSlot
            {
                Id = Guid.NewGuid(),
                BlockDate = currentDate,
                StartTime = startTime,
                EndTime = endTime,
                Reason = dto.Reason,
                EmployeeId = dto.EmployeeId,
                CreatedAt = DateTime.UtcNow,
            };

            _context.BlockedTimeSlots.Add(blockedSlot);
            createdSlots.Add(ToDto(blockedSlot));

            currentDate = currentDate.AddDays(1);
        }

        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Blocked time slots created in range: {FromDate} - {ToDate}, Count: {Count}, Employee: {EmployeeId}",
            fromDate, toDate, createdSlots.Count, dto.EmployeeId);

        return createdSlots;
    }

    // ── UPDATE ────────────────────────────────────────────────────

    /// <summary>
    /// Update an existing blocked time slot.
    /// Ownership check is performed in the controller before calling this.
    /// </summary>
    public async Task<BlockedTimeSlotDto> UpdateBlockedTimeSlotAsync(
        Guid id, UpdateBlockedTimeSlotDto dto)
    {
        var blockedSlot = await _context.BlockedTimeSlots.FindAsync(id);
        if (blockedSlot == null)
            throw new ArgumentException("Blockierter Zeitslot nicht gefunden");

        if (!DateOnly.TryParse(dto.BlockDate, out var blockDate))
            throw new ArgumentException("Ungültiges Datumsformat. Verwende YYYY-MM-DD");

        if (!TimeOnly.TryParse(dto.StartTime, out var startTime))
            throw new ArgumentException("Ungültiges Zeitformat. Verwende HH:mm");

        if (!TimeOnly.TryParse(dto.EndTime, out var endTime))
            throw new ArgumentException("Ungültiges Zeitformat. Verwende HH:mm");

        if (startTime >= endTime)
            throw new InvalidOperationException("Startzeit muss vor Endzeit liegen");

        if (blockDate < DateOnly.FromDateTime(DateTime.UtcNow.Date))
            throw new ArgumentException("Datum kann nicht in der Vergangenheit liegen");

        // Overlap check — exclude self, scope to same employee
        var overlapQuery = _context.BlockedTimeSlots
            .Where(b =>
                b.Id != id &&
                b.BlockDate == blockDate &&
                b.StartTime < endTime &&
                b.EndTime > startTime);

        if (blockedSlot.EmployeeId.HasValue)
            overlapQuery = overlapQuery.Where(b =>
                b.EmployeeId == blockedSlot.EmployeeId || b.EmployeeId == null);

        if (await overlapQuery.AnyAsync())
            throw new InvalidOperationException(
                "Zeitslot überschneidet sich mit einem bestehenden blockierten Zeitraum");

        // Booking conflict check — scoped to same employee
        var bookingQuery = _context.Bookings
            .Where(b =>
                b.BookingDate == blockDate &&
                b.Status != BookingStatus.Cancelled &&
                b.StartTime < endTime &&
                b.EndTime > startTime);

        if (blockedSlot.EmployeeId.HasValue)
            bookingQuery = bookingQuery.Where(b => b.EmployeeId == blockedSlot.EmployeeId);

        if (await bookingQuery.AnyAsync())
            throw new InvalidOperationException(
                "Kann Zeitslot nicht blockieren – es existieren bereits Buchungen in diesem Zeitraum");

        blockedSlot.BlockDate = blockDate;
        blockedSlot.StartTime = startTime;
        blockedSlot.EndTime = endTime;
        blockedSlot.Reason = dto.Reason;

        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Blocked time slot updated: {Id} for {Date} {Start}-{End}",
            id, blockDate, startTime, endTime);

        return ToDto(blockedSlot);
    }

    // ── DELETE ────────────────────────────────────────────────────

    /// <summary>
    /// Delete a blocked time slot by ID.
    /// Ownership check is performed in the controller before calling this.
    /// </summary>
    public async Task DeleteBlockedTimeSlotAsync(Guid id)
    {
        var blockedSlot = await _context.BlockedTimeSlots.FindAsync(id);
        if (blockedSlot == null)
            throw new ArgumentException("Blockierter Zeitslot nicht gefunden");

        _context.BlockedTimeSlots.Remove(blockedSlot);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Blocked time slot deleted: {Id}", id);
    }

    // ── Private helpers ───────────────────────────────────────────

    private static BlockedTimeSlotDto ToDto(BlockedTimeSlot b) =>
        new(b.Id, b.BlockDate, b.StartTime, b.EndTime, b.Reason, b.CreatedAt, b.EmployeeId);
}