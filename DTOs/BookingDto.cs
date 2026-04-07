// GentleBook.Api/DTOs/BookingDtos.cs
namespace GentleBook.Api.DTOs;

public record CreateBookingDto(
    Guid ServiceId,
    string BookingDate,      // YYYY-MM-DD
    string StartTime,        // HH:mm
    CustomerInfoDto Customer,
    string? CustomerNotes,
    Guid? EmployeeId        // optional – chosen employee
);

public record CustomerInfoDto(
    string FirstName,
    string LastName,
    string? Email,
    string? Phone
);

public record BookingResponseDto(
    Guid Id,
    string BookingNumber,
    string Status,
    bool ConfirmationSent,
    BookingDetailsDto Booking,
    CustomerToBookingDto Customer,
    EmployeeDto? Employee
);

public record BookingDetailsDto(
    Guid ServiceId,
    string ServiceName,
    string BookingDate,
    string StartTime,
    string EndTime,
    decimal Price,
    string Currency
);

public record CustomerToBookingDto(
    string FirstName,
    string LastName,
    string? Email
);

public record EmployeeDto(
    Guid Id,
    string Name,
    string Role,
    string? Specialty
);

public record CancelBookingDto(
    string? Reason,
    bool NotifyCustomer = true
);

public record CancelBookingResponseDto(
    bool Success,
    string Message,
    bool RefundIssued = false
);