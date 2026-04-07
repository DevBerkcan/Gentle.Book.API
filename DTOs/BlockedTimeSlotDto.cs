// GentleBook.Api/DTOs/BlockedTimeSlotDtos.cs
namespace GentleBook.Api.DTOs;

public record BlockedTimeSlotDto(
    Guid Id,
    DateOnly BlockDate,
    TimeOnly StartTime,
    TimeOnly EndTime,
    string? Reason,
    DateTime CreatedAt,
    Guid? EmployeeId   // needed for ownership checks in the controller
);

// For single day blocking
public record CreateBlockedTimeSlotDto(
    string BlockDate,              // Format: "YYYY-MM-DD"
    string StartTime,              // Format: "HH:mm"
    string EndTime,                // Format: "HH:mm"
    string? Reason,
    Guid? EmployeeId = null       // injected by controller from JWT claims
);

// For date range blocking
public record CreateBlockedDateRangeDto(
    string FromDate,               // Format: "YYYY-MM-DD"
    string ToDate,                 // Format: "YYYY-MM-DD"
    string StartTime,              // Format: "HH:mm"
    string EndTime,                // Format: "HH:mm"
    string? Reason,
    Guid? EmployeeId = null       // injected by controller from JWT claims
);

public record UpdateBlockedTimeSlotDto(
    string BlockDate,
    string StartTime,
    string EndTime,
    string? Reason
);