// Services/ReviewRequestService.cs
// Sends the "how was your appointment" email once a booking has been auto-completed
// (BookingCompletionService) or manually marked Completed by staff. Agency-exclusive, same as
// the auto-complete job. Booking.ReviewRequestSentAt guards against sending twice.
using GentleBook.Api.Configuration;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Services;

public class ReviewRequestService
{
    private readonly GentleBookDbContext _context;
    private readonly EmailService _emailService;
    private readonly ILogger<ReviewRequestService> _logger;

    public ReviewRequestService(GentleBookDbContext context, EmailService emailService, ILogger<ReviewRequestService> logger)
    {
        _context = context;
        _emailService = emailService;
        _logger = logger;
    }

    public async Task SendReviewRequestsAsync()
    {
        var agencyTenantIds = await _context.Subscriptions.IgnoreQueryFilters()
            .Where(s => s.Plan == SubscriptionPlan.Agency)
            .Select(s => s.TenantId)
            .ToListAsync();
        if (agencyTenantIds.Count == 0) return;

        var candidates = await _context.Bookings.IgnoreQueryFilters()
            .Include(b => b.Customer)
            .Include(b => b.Service)
            .Include(b => b.Tenant)
            .Where(b =>
                agencyTenantIds.Contains(b.TenantId) &&
                b.Status == BookingStatus.Completed &&
                b.ReviewRequestSentAt == null)
            .ToListAsync();

        if (candidates.Count == 0) return;

        var sent = 0;
        foreach (var booking in candidates)
        {
            if (string.IsNullOrWhiteSpace(booking.Customer.Email))
            {
                booking.ReviewRequestSentAt = DateTime.UtcNow; // nothing to send to — don't retry forever
                continue;
            }

            var settings = await _context.TenantSettings.IgnoreQueryFilters()
                .FirstOrDefaultAsync(s => s.TenantId == booking.TenantId);

            var ok = await _emailService.SendReviewRequestEmailAsync(
                booking,
                booking.Customer,
                booking.Service,
                settings?.CompanyName ?? booking.Tenant.Name,
                settings?.LogoUrl,
                settings?.PrimaryColor ?? "#8B7BC7");

            if (ok)
            {
                booking.ReviewRequestSentAt = DateTime.UtcNow;
                sent++;
            }
        }

        await _context.SaveChangesAsync();
        _logger.LogInformation("Review requests: {Sent}/{Total} sent across {TenantCount} Agency tenant(s).",
            sent, candidates.Count, agencyTenantIds.Count);
    }
}
