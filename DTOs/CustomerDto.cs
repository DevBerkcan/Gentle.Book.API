namespace GentleBook.Api.DTOs;

// ── Customer List ───────────────────────────────────────────────

public record CustomerListItemDto(
    Guid Id,
    string FullName,
    string? Email,
    string? Phone,
    int TotalBookings,
    DateTime? LastVisit,
    DateTime CreatedAt
);

// ── Customer Details ────────────────────────────────────────────

public record CustomerDetailDto(
    Guid Id,
    string FirstName,
    string LastName,
    string? Email,
    string? Phone,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? LastVisit,
    int TotalBookings,
    int NoShowCount,
    string? Notes,
    string FullName,
    Guid? EmployeeId,
    List<CustomerBookingItemDto> RecentBookings
);

public record CustomerBookingItemDto(
    Guid Id,
    string BookingNumber,
    string ServiceName,
    string BookingDate,
    string StartTime,
    string EndTime,
    string Status,
    decimal Price,
    string Currency
);

// ── Customer CRUD ───────────────────────────────────────────────

public record CreateCustomerRequestDto(
    string FirstName,
    string LastName,
    string? Email,
    string? Phone,
    string? Notes
);

public record UpdateCustomerRequestDto(
    string FirstName,
    string LastName,
    string? Email,
    string? Phone,
    string? Notes
);

public record CustomerResponseDto(
    Guid Id,
    string FirstName,
    string LastName,
    string? Email,
    string? Phone,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? LastVisit,
    int TotalBookings,
    int NoShowCount,
    string? Notes,
    string FullName,
    Guid? EmployeeId
);