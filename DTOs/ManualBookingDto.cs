// DTOs/ManualBookingDto.cs
namespace GentleBook.Api.DTOs;

public record CreateManualBookingDto(
    Guid ServiceId,
    string BookingDate,
    string StartTime,
    string FirstName,
    string LastName,
    string? Email,
    string? Phone,
    string? CustomerNotes,
    Guid? EmployeeId    
);

public record ManualBookingResponseDto(
    Guid Id,
    string BookingNumber,
    string Status,
    bool ConfirmationSent,
    BookingDetailsDto Booking,
    CustomerBasicDto Customer,
    EmployeeDto? Employee
);

public record CustomerBasicDto(
    string FirstName,
    string LastName,
    string? Email,
    Guid? EmployeeId
);

public record EmailConflictCheckDto
{
    public bool HasConflict { get; set; }
    public string? ExistingName { get; set; }
    public string? ExistingEmail { get; set; }
}