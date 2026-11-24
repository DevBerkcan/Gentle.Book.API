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
    Guid? EmployeeId,
    string? VoucherCode,
    // Optional: book this appointment as a session of a treatment package. When set, the
    // booking is made for the package's own customer (not matched by name/email).
    Guid? TreatmentPackageId = null
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