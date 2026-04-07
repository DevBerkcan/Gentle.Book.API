namespace GentleBook.Api.DTOs;

public class TrackLinkClickDto
{
    public string LinkName { get; set; } = string.Empty;
    public string LinkUrl { get; set; } = string.Empty;
    public string? SessionId { get; set; }
}

public class SimplifiedTrackingStatisticsDto
{
    // Overview stats
    public int TotalBookings { get; set; }
    public int TotalPageViews { get; set; }
    public int TotalLinkClicks { get; set; }

    // Revenue split by currency
    public decimal TotalRevenueCHF { get; set; }
    public decimal TotalRevenueEUR { get; set; }

    // Average booking value split by currency
    public decimal AverageBookingValueCHF { get; set; }
    public decimal AverageBookingValueEUR { get; set; }

    // Link click statistics
    public List<LinkClickStatisticDto> LinkClicks { get; set; } = new();
}

public class LinkClickStatisticDto
{
    public string LinkName { get; set; } = string.Empty;
    public int ClickCount { get; set; }
    public double Percentage { get; set; }
}

public class RevenueStatisticsDto
{
    // Revenue split by currency
    public decimal TodayRevenueCHF { get; set; }
    public decimal TodayRevenueEUR { get; set; }
    public decimal WeekRevenueCHF { get; set; }
    public decimal WeekRevenueEUR { get; set; }
    public decimal MonthRevenueCHF { get; set; }
    public decimal MonthRevenueEUR { get; set; }
    public decimal AllTimeRevenueCHF { get; set; }
    public decimal AllTimeRevenueEUR { get; set; }

    // Booking counts (same for both currencies)
    public int TodayBookings { get; set; }
    public int WeekBookings { get; set; }
    public int MonthBookings { get; set; }
    public int AllTimeBookings { get; set; }
}

public class TrackPageViewDto
{
    public string? PageUrl { get; set; }
    public string? ReferrerUrl { get; set; }
    public string? UtmSource { get; set; }
    public string? UtmMedium { get; set; }
    public string? UtmCampaign { get; set; }
    public string? UtmContent { get; set; }
    public string? UtmTerm { get; set; }
    public string? SessionId { get; set; }
}