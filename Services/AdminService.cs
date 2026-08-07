using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.DTOs;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Services;

public class AdminService
{
    private readonly GentleBookDbContext _context;
    private readonly ILogger<AdminService> _logger;
    private readonly EmailService _emailService;
    private readonly LoyaltyService _loyaltyService;

    public AdminService(GentleBookDbContext context, ILogger<AdminService> logger, EmailService emailService, LoyaltyService loyaltyService)
    {
        _context = context;
        _logger = logger;
        _emailService = emailService;
        _loyaltyService = loyaltyService;
    }

    public async Task<DashboardOverviewDto> GetDashboardOverviewAsync(Guid? employeeId = null)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var now = DateTime.UtcNow;

        // Base query with employee filter
        var bookingsQuery = _context.Bookings
            .Include(b => b.Customer)
            .Include(b => b.Service)
            .AsQueryable();

        if (employeeId.HasValue)
            bookingsQuery = bookingsQuery.Where(b => b.EmployeeId == employeeId.Value);

        var todayBookings = await bookingsQuery
            .Where(b => b.BookingDate == today)
            .OrderBy(b => b.StartTime)
            .Select(b => new
            {
                Booking = b,
                CustomerName = b.Customer != null
                    ? $"{b.Customer.FirstName ?? ""} {b.Customer.LastName ?? ""}".Trim()
                    : "Unknown Customer",
                CustomerEmail = b.Customer != null ? b.Customer.Email : null,
                CustomerPhone = b.Customer != null ? b.Customer.Phone : null,
                ServiceName = b.Service != null ? b.Service.Name : "Unknown Service",
                ServicePrice = b.Service != null ? b.Service.Price : 0,
                ServiceCurrency = b.Service != null ? b.Service.Currency : "CHF"
            })
            .ToListAsync();

        var todayOverview = new TodayOverviewDto(
            today.ToString("yyyy-MM-dd"),
            todayBookings.Count,
            todayBookings.Count(b => b.Booking.Status == BookingStatus.Completed),
            todayBookings.Count(b => b.Booking.Status == BookingStatus.Pending || b.Booking.Status == BookingStatus.Confirmed),
            todayBookings.Count(b => b.Booking.Status == BookingStatus.Cancelled),
            todayBookings.Where(b => b.Booking.Status == BookingStatus.Completed).Sum(b => b.ServicePrice),
            todayBookings.Select(b => new BookingListItemDto(
                b.Booking.Id,
                Booking.GenerateBookingNumber(b.Booking.BookingDate, b.Booking.Id),
                b.Booking.Status.ToString(),
                b.ServiceName,
                b.CustomerName,
                b.CustomerEmail,
                b.CustomerPhone,
                b.Booking.BookingDate.ToString("yyyy-MM-dd"),
                b.Booking.StartTime.ToString("HH:mm"),
                b.Booking.EndTime.ToString("HH:mm"),
                b.ServicePrice,
                b.ServiceCurrency,
                b.Booking.CustomerNotes,
                b.Booking.CreatedAt
            )).ToList()
        );

        var nextBooking = await bookingsQuery
            .Where(b =>
                (b.BookingDate > today || (b.BookingDate == today && b.StartTime > TimeOnly.FromDateTime(now))) &&
                (b.Status == BookingStatus.Pending || b.Status == BookingStatus.Confirmed))
            .OrderBy(b => b.BookingDate)
            .ThenBy(b => b.StartTime)
            .Select(b => new
            {
                b.Id,
                b.BookingDate,
                b.StartTime,
                b.EndTime,
                ServiceName = b.Service != null ? b.Service.Name : "Unknown Service",
                CustomerName = b.Customer != null
                    ? $"{b.Customer.FirstName ?? ""} {b.Customer.LastName ?? ""}".Trim()
                    : "Unknown Customer"
            })
            .FirstOrDefaultAsync();

        UpcomingBookingDto? upcomingBooking = null;
        if (nextBooking != null)
        {
            var bookingDateTime = nextBooking.BookingDate.ToDateTime(nextBooking.StartTime);
            var minutesUntil = (int)(bookingDateTime - now).TotalMinutes;

            upcomingBooking = new UpcomingBookingDto(
                nextBooking.Id,
                Booking.GenerateBookingNumber(nextBooking.BookingDate, nextBooking.Id),
                nextBooking.ServiceName,
                nextBooking.CustomerName,
                nextBooking.BookingDate.ToString("yyyy-MM-dd"),
                nextBooking.StartTime.ToString("HH:mm"),
                nextBooking.EndTime.ToString("HH:mm"),
                minutesUntil
            );
        }

        var statistics = await GetDashboardStatisticsAsync(employeeId);

        return new DashboardOverviewDto(todayOverview, upcomingBooking, statistics);
    }

    public async Task<DashboardStatisticsDto> GetDashboardStatisticsAsync(Guid? employeeId = null)
    {
        var now = DateTime.UtcNow;
        var startOfMonth = DateTime.SpecifyKind(new DateTime(now.Year, now.Month, 1), DateTimeKind.Utc);
        var startOfLastMonth = startOfMonth.AddMonths(-1);
        var endOfLastMonth = startOfMonth.AddDays(-1);

        // Base query with employee filter
        var bookingsQuery = _context.Bookings
            .Include(b => b.Service)
            .AsQueryable();

        if (employeeId.HasValue)
            bookingsQuery = bookingsQuery.Where(b => b.EmployeeId == employeeId.Value);

        // This month bookings with currency
        var thisMonthBookings = await bookingsQuery
            .Where(b => b.CreatedAt >= startOfMonth && b.Status != BookingStatus.Cancelled && b.Service != null)
            .Select(b => new {
                b.Status,
                Price = b.Service.Price,
                Currency = b.Service.Currency
            })
            .ToListAsync();

        // Last month bookings with currency
        var lastMonthBookings = await bookingsQuery
            .Where(b => b.CreatedAt >= startOfLastMonth && b.CreatedAt <= endOfLastMonth && b.Status != BookingStatus.Cancelled && b.Service != null)
            .Select(b => new {
                b.Status,
                Price = b.Service.Price,
                Currency = b.Service.Currency
            })
            .ToListAsync();

        // Calculate revenue by currency for this month
        var revenueThisMonthCHF = thisMonthBookings
            .Where(b => b.Currency == "CHF")
            .Sum(b => b.Price);

        var revenueThisMonthEUR = thisMonthBookings
            .Where(b => b.Currency != "CHF")
            .Sum(b => b.Price);

        // Calculate revenue by currency for last month
        var revenueLastMonthCHF = lastMonthBookings
            .Where(b => b.Currency == "CHF")
            .Sum(b => b.Price);

        var revenueLastMonthEUR = lastMonthBookings
            .Where(b => b.Currency != "CHF")
            .Sum(b => b.Price);
        var revenueThisMonthByCurrency = thisMonthBookings
            .GroupBy(booking => booking.Currency.ToUpperInvariant())
            .ToDictionary(group => group.Key, group => group.Sum(booking => booking.Price));
        var revenueLastMonthByCurrency = lastMonthBookings
            .GroupBy(booking => booking.Currency.ToUpperInvariant())
            .ToDictionary(group => group.Key, group => group.Sum(booking => booking.Price));

        var customersQuery = _context.Customers.AsQueryable();
        if (employeeId.HasValue)
            customersQuery = customersQuery.Where(c => c.EmployeeId == employeeId.Value);

        var totalCustomers = await customersQuery.CountAsync();
        var newCustomersThisMonth = await customersQuery
            .CountAsync(c => c.CreatedAt >= startOfMonth);

        var completedBookings = await bookingsQuery
            .Where(b => (b.Status == BookingStatus.Completed || b.Status == BookingStatus.Confirmed) && b.Service != null)
            .Select(b => new
            {
                ServiceId = b.Service.Id,
                ServiceName = b.Service.Name,
                Price = b.Service.Price,
                Currency = b.Service.Currency
            })
            .ToListAsync();

        var popularServices = completedBookings
            .GroupBy(b => new { b.ServiceId, b.ServiceName })
            .Select(g => new PopularServiceDto(
                g.Key.ServiceName ?? "Unknown",
                g.Count(),
                g.Where(x => x.Currency == "CHF").Sum(x => x.Price),
                g.Where(x => x.Currency != "CHF").Sum(x => x.Price)
            ))
            .OrderByDescending(s => s.BookingCount)
            .Take(5)
            .ToList();

        return new DashboardStatisticsDto(
            thisMonthBookings.Count,
            lastMonthBookings.Count,
            revenueThisMonthCHF,
            revenueLastMonthCHF,
            revenueThisMonthEUR,
            revenueLastMonthEUR,
            totalCustomers,
            newCustomersThisMonth,
            popularServices,
            revenueThisMonthByCurrency,
            revenueLastMonthByCurrency
        );
    }

    public async Task<PagedResponseDto<BookingListItemDto>> GetBookingsAsync(BookingFilterDto filter)
    {
        var query = _context.Bookings
            .Include(b => b.Customer)
            .Include(b => b.Service)
            .AsQueryable();

        // Apply employee filter if specified
        if (filter.EmployeeId.HasValue)
            query = query.Where(b => b.EmployeeId == filter.EmployeeId.Value);

        // Apply status filter
        if (!string.IsNullOrEmpty(filter.Status))
        {
            if (Enum.TryParse<BookingStatus>(filter.Status, true, out var status))
                query = query.Where(b => b.Status == status);
        }

        // Apply date filters
        if (filter.FromDate.HasValue)
        {
            var fromDate = DateOnly.FromDateTime(filter.FromDate.Value);
            query = query.Where(b => b.BookingDate >= fromDate);
        }

        if (filter.ToDate.HasValue)
        {
            var toDate = DateOnly.FromDateTime(filter.ToDate.Value);
            query = query.Where(b => b.BookingDate <= toDate);
        }

        // Apply service filter
        if (filter.ServiceId.HasValue)
            query = query.Where(b => b.ServiceId == filter.ServiceId.Value);

        // Apply search filter
        if (!string.IsNullOrEmpty(filter.SearchTerm))
        {
            var search = filter.SearchTerm.ToLower();

            query = query.Where(b =>
                (b.Customer != null && b.Customer.FirstName.ToLower().Contains(search)) ||
                (b.Customer != null && b.Customer.LastName.ToLower().Contains(search)) ||
                (b.Customer != null && b.Customer.Email != null && b.Customer.Email.ToLower().Contains(search)) ||
                (b.Customer != null && b.Customer.Phone != null && b.Customer.Phone.Contains(search)) ||
                (b.Service != null && b.Service.Name.ToLower().Contains(search))
            );
        }

        var totalCount = await query.CountAsync();

        var bookings = await query
            .OrderByDescending(b => b.BookingDate)
            .ThenByDescending(b => b.StartTime)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync();

        var items = bookings.Select(b =>
        {
            // Create customer name safely
            string customerName = "Unknown Customer";
            if (b.Customer != null)
            {
                var firstName = b.Customer.FirstName ?? "";
                var lastName = b.Customer.LastName ?? "";
                customerName = $"{firstName} {lastName}".Trim();
                if (string.IsNullOrWhiteSpace(customerName))
                    customerName = "Unknown Customer";
            }

            return new BookingListItemDto(
                b.Id,
                Booking.GenerateBookingNumber(b.BookingDate, b.Id),
                b.Status.ToString(),
                b.Service?.Name ?? "Unknown Service",
                customerName,
                b.Customer?.Email,
                b.Customer?.Phone,
                b.BookingDate.ToString("yyyy-MM-dd"),
                b.StartTime.ToString("HH:mm"),
                b.EndTime.ToString("HH:mm"),
                b.Service?.Price ?? 0,
                b.Service?.Currency ?? "CHF",
                b.CustomerNotes,
                b.CreatedAt
            );
        }).ToList();

        var totalPages = (int)Math.Ceiling(totalCount / (double)filter.PageSize);

        return new PagedResponseDto<BookingListItemDto>(
            items,
            totalCount,
            filter.Page,
            filter.PageSize,
            totalPages
        );
    }

    private static bool IsValidStatusTransition(BookingStatus from, BookingStatus to) => (from, to) switch
    {
        (BookingStatus.Pending,   BookingStatus.Confirmed) => true,
        (BookingStatus.Pending,   BookingStatus.Cancelled) => true,
        (BookingStatus.Confirmed, BookingStatus.Completed) => true,
        (BookingStatus.Confirmed, BookingStatus.Cancelled) => true,
        (BookingStatus.Confirmed, BookingStatus.NoShow)    => true,
        _ => false,
    };

    public async Task<BookingListItemDto> UpdateBookingStatusAsync(Guid bookingId, UpdateBookingStatusDto dto)
    {
        var booking = await _context.Bookings
            .Include(b => b.Customer)
            .Include(b => b.Service)
            .FirstOrDefaultAsync(b => b.Id == bookingId);

        if (booking == null)
            throw new ArgumentException("Buchung nicht gefunden");

        if (!Enum.TryParse<BookingStatus>(dto.Status, true, out var newStatus))
            throw new ArgumentException($"Ungültiger Status: {dto.Status}");

        var oldStatus = booking.Status;

        // State-Machine: nur fachlich sinnvolle Übergänge zulassen.
        // Completed/Cancelled/NoShow sind Endzustände.
        if (oldStatus != newStatus && !IsValidStatusTransition(oldStatus, newStatus))
            throw new InvalidOperationException(
                $"Statuswechsel von \"{oldStatus}\" nach \"{newStatus}\" ist nicht zulässig.");

        booking.Status = newStatus;
        booking.AdminNotes = dto.AdminNotes;
        booking.UpdatedAt = DateTime.UtcNow;

        if (newStatus == BookingStatus.Cancelled && !booking.CancelledAt.HasValue)
            booking.CancelledAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        // Same trigger point as the automatic completion job (BookingCompletionService) — a
        // customer earns points whether staff completed the booking by hand or it auto-completed.
        // No-ops for non-Agency tenants / disabled feature (see LoyaltyService).
        if (newStatus == BookingStatus.Completed && oldStatus != BookingStatus.Completed)
            await _loyaltyService.AwardPointsForBookingAsync(booking.Id);

        _logger.LogInformation(
            "Booking {BookingId} status updated from {OldStatus} to {NewStatus} by admin",
            bookingId, oldStatus, newStatus
        );

        // Notify customer when admin cancels their booking
        if (newStatus == BookingStatus.Cancelled && oldStatus != BookingStatus.Cancelled
            && booking.Customer?.Email != null && booking.Service != null)
        {
            try
            {
                await _emailService.SendCancellationConfirmationAsync(booking, booking.Customer, booking.Service);
                _logger.LogInformation("Cancellation email sent to {Email} for booking {BookingId}", booking.Customer.Email, bookingId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send cancellation email for booking {BookingId}", bookingId);
            }
        }

        var customerName = booking.Customer != null
            ? $"{booking.Customer.FirstName ?? ""} {booking.Customer.LastName ?? ""}".Trim()
            : "Unknown Customer";

        if (string.IsNullOrWhiteSpace(customerName))
            customerName = "Unknown Customer";

        return new BookingListItemDto(
            booking.Id,
            Booking.GenerateBookingNumber(booking.BookingDate, booking.Id),
            booking.Status.ToString(),
            booking.Service?.Name ?? "Unknown Service",
            customerName,
            booking.Customer?.Email,
            booking.Customer?.Phone,
            booking.BookingDate.ToString("yyyy-MM-dd"),
            booking.StartTime.ToString("HH:mm"),
            booking.EndTime.ToString("HH:mm"),
            booking.Service?.Price ?? 0,
            booking.Service?.Currency ?? "CHF",
            booking.CustomerNotes,
            booking.CreatedAt
        );
    }

    public async Task<DeleteBookingResponseDto> DeleteBookingAsync(Guid bookingId, string? reason)
    {
        var booking = await _context.Bookings
            .Include(b => b.Customer)
            .Include(b => b.Service)
            .Include(b => b.EmailLogs)
            .FirstOrDefaultAsync(b => b.Id == bookingId);

        if (booking == null)
        {
            throw new ArgumentException("Buchung nicht gefunden");
        }

        _logger.LogInformation(
            "Admin deleting booking {BookingId} for customer {CustomerEmail}. Reason: {Reason}",
            bookingId,
            booking.Customer?.Email ?? "unknown",
            reason ?? "No reason provided"
        );

        // Notify customer before deleting
        if (booking.Customer?.Email != null && booking.Service != null && booking.Status != BookingStatus.Cancelled)
        {
            try
            {
                await _emailService.SendCancellationConfirmationAsync(booking, booking.Customer, booking.Service);
                _logger.LogInformation("Cancellation email sent to {Email} for deleted booking {BookingId}", booking.Customer.Email, bookingId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send cancellation email for deleted booking {BookingId}", bookingId);
            }
        }

        // Remove related email logs first (if cascade delete is not set)
        if (booking.EmailLogs != null && booking.EmailLogs.Any())
        {
            _context.EmailLogs.RemoveRange(booking.EmailLogs);
        }

        // Remove the booking
        _context.Bookings.Remove(booking);

        await _context.SaveChangesAsync();

        return new DeleteBookingResponseDto(
            true,
            "Buchung wurde erfolgreich gelöscht"
        );
    }
}
