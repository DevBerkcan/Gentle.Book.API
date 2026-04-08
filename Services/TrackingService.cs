using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.DTOs;
using Microsoft.EntityFrameworkCore;
using GentleBook.Api.Data.Entities;

namespace GentleBook.Api.Services;

public class TrackingService
{
    private readonly GentleBookDbContext _context;
    private readonly ILogger<TrackingService> _logger;

    public TrackingService(GentleBookDbContext context, ILogger<TrackingService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task TrackLinkClickAsync(TrackLinkClickDto dto, string referrerUrl)
    {
        var linkClick = new LinkClick
        {
            Id = Guid.NewGuid(),
            LinkName = dto.LinkName,
            LinkUrl = dto.LinkUrl,
            SessionId = dto.SessionId,
            ReferrerUrl = referrerUrl,
            ClickedAt = DateTime.UtcNow
        };

        _context.LinkClicks.Add(linkClick);
        await _context.SaveChangesAsync();

        _logger.LogDebug("Link click tracked: {LinkName}", dto.LinkName);
    }

    public async Task<SimplifiedTrackingStatisticsDto> GetTrackingStatisticsAsync(DateTime? fromDate, DateTime? toDate)
    {
        // If no dates provided, get ALL data
        var queryFromDate = fromDate ?? DateTime.MinValue;  // All time from beginning
        var queryToDate = toDate ?? DateTime.UtcNow;        // Up to now

        // Total page views
        var totalPageViews = await _context.PageViews
            .Where(p => p.ViewedAt >= queryFromDate && p.ViewedAt <= queryToDate)
            .CountAsync();

        // Total bookings and revenue split by currency
        var bookings = await _context.Bookings
            .Include(b => b.Service)
            .Where(b => b.CreatedAt >= queryFromDate && b.CreatedAt <= queryToDate
                        && b.Status != BookingStatus.Cancelled)
            .Select(b => new
            {
                b.Service.Price,
                b.Service.Currency
            })
            .ToListAsync();

        var totalBookings = bookings.Count;

        // Split revenue by currency
        var totalRevenueCHF = bookings
            .Where(b => b.Currency == "CHF")
            .Sum(b => b.Price);

        var totalRevenueEUR = bookings
            .Where(b => b.Currency == "EUR")
            .Sum(b => b.Price);

        // Average booking values by currency
        var averageBookingValueCHF = totalBookings > 0 ? totalRevenueCHF / totalBookings : 0;
        var averageBookingValueEUR = totalBookings > 0 ? totalRevenueEUR / totalBookings : 0;

        // Total link clicks
        var totalLinkClicks = await _context.LinkClicks
            .Where(l => l.ClickedAt >= queryFromDate && l.ClickedAt <= queryToDate)
            .CountAsync();

        // Link click statistics grouped by link name
        var linkClicks = await _context.LinkClicks
            .Where(l => l.ClickedAt >= queryFromDate && l.ClickedAt <= queryToDate)
            .GroupBy(l => l.LinkName)
            .Select(g => new
            {
                LinkName = g.Key,
                ClickCount = g.Count()
            })
            .OrderByDescending(x => x.ClickCount)
            .ToListAsync();

        var totalClicksForPercentage = linkClicks.Sum(x => x.ClickCount);

        var linkClickStats = linkClicks.Select(x => new LinkClickStatisticDto
        {
            LinkName = x.LinkName,
            ClickCount = x.ClickCount,
            Percentage = totalClicksForPercentage > 0
                ? Math.Round((double)x.ClickCount / totalClicksForPercentage * 100, 1)
                : 0
        }).ToList();

        return new SimplifiedTrackingStatisticsDto
        {
            TotalBookings = totalBookings,
            TotalPageViews = totalPageViews,
            TotalLinkClicks = totalLinkClicks,
            TotalRevenueCHF = Math.Round(totalRevenueCHF, 2),
            TotalRevenueEUR = Math.Round(totalRevenueEUR, 2),
            AverageBookingValueCHF = Math.Round(averageBookingValueCHF, 2),
            AverageBookingValueEUR = Math.Round(averageBookingValueEUR, 2),
            LinkClicks = linkClickStats
        };
    }

    public async Task<RevenueStatisticsDto> GetRevenueStatisticsAsync()
    {
        var today = DateTime.UtcNow.Date;
        var weekAgo = today.AddDays(-7);
        var monthAgo = today.AddDays(-30);

        // Today's bookings with currency
        var todayBookings = await _context.Bookings
            .Include(b => b.Service)
            .Where(b => b.CreatedAt.Date == today
                        && b.Status != BookingStatus.Cancelled)
            .Select(b => new
            {
                b.Service.Price,
                b.Service.Currency
            })
            .ToListAsync();

        // This week's bookings with currency
        var weekBookings = await _context.Bookings
            .Include(b => b.Service)
            .Where(b => b.CreatedAt >= weekAgo
                        && b.Status != BookingStatus.Cancelled)
            .Select(b => new
            {
                b.Service.Price,
                b.Service.Currency
            })
            .ToListAsync();

        // This month's bookings with currency
        var monthBookings = await _context.Bookings
            .Include(b => b.Service)
            .Where(b => b.CreatedAt >= monthAgo
                        && b.Status != BookingStatus.Cancelled)
            .Select(b => new
            {
                b.Service.Price,
                b.Service.Currency
            })
            .ToListAsync();

        // ALL TIME bookings with currency
        var allTimeBookings = await _context.Bookings
            .Include(b => b.Service)
            .Where(b => b.Status != BookingStatus.Cancelled)
            .Select(b => new
            {
                b.Service.Price,
                b.Service.Currency
            })
            .ToListAsync();

        // Calculate revenue by currency for each period
        var todayRevenueCHF = todayBookings
            .Where(b => b.Currency == "CHF")
            .Sum(b => b.Price);
        var todayRevenueEUR = todayBookings
            .Where(b => b.Currency == "EUR")
            .Sum(b => b.Price);

        var weekRevenueCHF = weekBookings
            .Where(b => b.Currency == "CHF")
            .Sum(b => b.Price);
        var weekRevenueEUR = weekBookings
            .Where(b => b.Currency == "EUR")
            .Sum(b => b.Price);

        var monthRevenueCHF = monthBookings
            .Where(b => b.Currency == "CHF")
            .Sum(b => b.Price);
        var monthRevenueEUR = monthBookings
            .Where(b => b.Currency == "EUR")
            .Sum(b => b.Price);

        var allTimeRevenueCHF = allTimeBookings
            .Where(b => b.Currency == "CHF")
            .Sum(b => b.Price);
        var allTimeRevenueEUR = allTimeBookings
            .Where(b => b.Currency == "EUR")
            .Sum(b => b.Price);

        return new RevenueStatisticsDto
        {
            TodayRevenueCHF = Math.Round(todayRevenueCHF, 2),
            TodayRevenueEUR = Math.Round(todayRevenueEUR, 2),
            TodayBookings = todayBookings.Count,

            WeekRevenueCHF = Math.Round(weekRevenueCHF, 2),
            WeekRevenueEUR = Math.Round(weekRevenueEUR, 2),
            WeekBookings = weekBookings.Count,

            MonthRevenueCHF = Math.Round(monthRevenueCHF, 2),
            MonthRevenueEUR = Math.Round(monthRevenueEUR, 2),
            MonthBookings = monthBookings.Count,

            AllTimeRevenueCHF = Math.Round(allTimeRevenueCHF, 2),
            AllTimeRevenueEUR = Math.Round(allTimeRevenueEUR, 2),
            AllTimeBookings = allTimeBookings.Count
        };
    }

    public async Task<(bool Success, string? ErrorMessage)> TrackPageViewAsync(TrackPageViewDto dto, string userAgent, string? ipAddress)
    {
        try
        {
            var pageView = new PageView
            {
                Id = Guid.NewGuid(),
                PageUrl = dto.PageUrl ?? "/",
                ReferrerUrl = dto.ReferrerUrl,
                UtmSource = dto.UtmSource,
                UtmMedium = dto.UtmMedium,
                UtmCampaign = dto.UtmCampaign,
                UtmContent = dto.UtmContent,
                UtmTerm = dto.UtmTerm,
                SessionId = dto.SessionId,
                UserAgent = userAgent,
                IpAddress = ipAddress,
                ViewedAt = DateTime.UtcNow
            };

            _context.PageViews.Add(pageView);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Page view tracked: {PageUrl} at {Time}", pageView.PageUrl, DateTime.UtcNow);
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error tracking page view");
            return (false, ex.Message);
        }
    }
}