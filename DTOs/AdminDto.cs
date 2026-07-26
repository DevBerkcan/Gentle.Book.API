namespace GentleBook.Api.DTOs;

// Dashboard Overview Response
public record DashboardOverviewDto(
    TodayOverviewDto Today,
    UpcomingBookingDto? NextBooking,
    DashboardStatisticsDto Statistics
);

public record TodayOverviewDto(
    string Date,
    int TotalBookings,
    int CompletedBookings,
    int PendingBookings,
    int CancelledBookings,
    decimal TotalRevenue,
    List<BookingListItemDto> Bookings
);

public record UpcomingBookingDto(
    Guid Id,
    string BookingNumber,
    string ServiceName,
    string CustomerName,
    string Date,
    string StartTime,
    string EndTime,
    int MinutesUntil
);

public record DashboardStatisticsDto(
    int TotalBookingsThisMonth,
    int TotalBookingsLastMonth,
    decimal RevenueThisMonthCHF,
    decimal RevenueLastMonthCHF,
    decimal RevenueThisMonthEUR,
    decimal RevenueLastMonthEUR,
    int TotalCustomers,
    int NewCustomersThisMonth,
    List<PopularServiceDto> PopularServices,
    Dictionary<string, decimal> RevenueThisMonthByCurrency,
    Dictionary<string, decimal> RevenueLastMonthByCurrency
);

public record PopularServiceDto(
    string ServiceName,
    int BookingCount,
    decimal RevenueCHF,
    decimal RevenueEUR
);

public record BookingListItemDto(
    Guid Id,
    string BookingNumber,
    string Status,
    string ServiceName,
    string CustomerName,
    string? CustomerEmail,
    string? CustomerPhone,
    string BookingDate,
    string StartTime,
    string EndTime,
    decimal Price,
    string Currency,
    string? CustomerNotes,
    DateTime CreatedAt
);

// Update Booking Status Request
public record UpdateBookingStatusDto(
    string Status,
    string? AdminNotes
);

// Booking Filter Request
public record BookingFilterDto(
    string? Status,
    DateTime? FromDate,
    DateTime? ToDate,
    Guid? ServiceId,
    string? SearchTerm,
    int Page = 1,
    int PageSize = 20,
    Guid? EmployeeId = null
);

// Paginated Response
public record PagedResponseDto<T>(
    List<T> Items,
    int TotalCount,
    int Page,
    int PageSize,
    int TotalPages
);

// DTOs/AdminDto.cs
public record DeleteBookingDto(
    string? Reason
);

public record DeleteBookingResponseDto(
    bool Success,
    string Message
);
