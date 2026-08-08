// Services/IntakeFormReminderService.cs
// Reminds a customer to fill out the intake form if they haven't, ~24h after booking and only
// while the appointment is still upcoming. Agency + allowed-industry only, mirrors
// ReviewRequestService.cs. Booking.IntakeFormReminderSentAt guards against sending twice.
using GentleBook.Api.Configuration;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Services;

public class IntakeFormReminderService
{
    private readonly GentleBookDbContext _context;
    private readonly EmailService _emailService;
    private readonly ILogger<IntakeFormReminderService> _logger;

    public IntakeFormReminderService(GentleBookDbContext context, EmailService emailService, ILogger<IntakeFormReminderService> logger)
    {
        _context = context;
        _emailService = emailService;
        _logger = logger;
    }

    public async Task SendRemindersAsync()
    {
        var now = DateTime.UtcNow;
        var createdBefore = now.AddHours(-24);

        var eligibleTenantIds = await _context.Subscriptions.IgnoreQueryFilters()
            .Where(s => s.Plan == SubscriptionPlan.Agency)
            .Select(s => s.TenantId)
            .ToListAsync();
        if (eligibleTenantIds.Count == 0) return;

        // Further narrow to tenants whose industry is allowed for this feature at all.
        var allowedTenantIds = await _context.Tenants.IgnoreQueryFilters()
            .Where(t => eligibleTenantIds.Contains(t.Id))
            .Select(t => new { t.Id, t.IndustryType })
            .ToListAsync();
        var tenantIds = allowedTenantIds
            .Where(t => IntakeFormIndustryGate.IsAllowed(t.IndustryType))
            .Select(t => t.Id)
            .ToList();
        if (tenantIds.Count == 0) return;

        var today = DateOnly.FromDateTime(now);

        var candidates = await _context.Bookings.IgnoreQueryFilters()
            .Include(b => b.Customer)
            .Include(b => b.Service)
            .Include(b => b.Tenant)
            .Where(b =>
                tenantIds.Contains(b.TenantId) &&
                b.Status == BookingStatus.Confirmed &&
                b.BookingDate >= today &&
                b.CreatedAt <= createdBefore &&
                b.IntakeFormReminderSentAt == null)
            .ToListAsync();

        if (candidates.Count == 0) return;

        var sent = 0;
        foreach (var booking in candidates)
        {
            var alreadySubmitted = await _context.IntakeFormResponses.IgnoreQueryFilters()
                .AnyAsync(r => r.BookingId == booking.Id);
            if (alreadySubmitted)
            {
                booking.IntakeFormReminderSentAt = now;
                continue;
            }

            var hasActiveField = await _context.IntakeFormFields.IgnoreQueryFilters()
                .AnyAsync(f => f.TenantId == booking.TenantId && f.IsActive &&
                    (f.CategoryId == null || f.CategoryId == booking.Service.CategoryId));
            if (!hasActiveField)
            {
                booking.IntakeFormReminderSentAt = now; // nothing to remind about — don't retry forever
                continue;
            }

            if (string.IsNullOrWhiteSpace(booking.Customer.Email))
            {
                booking.IntakeFormReminderSentAt = now;
                continue;
            }

            var settings = await _context.TenantSettings.IgnoreQueryFilters()
                .FirstOrDefaultAsync(s => s.TenantId == booking.TenantId);

            var ok = await _emailService.SendIntakeFormReminderEmailAsync(
                booking,
                booking.Customer,
                settings?.CompanyName ?? booking.Tenant.Name,
                settings?.LogoUrl,
                settings?.PrimaryColor ?? "#8B7BC7");

            if (ok)
            {
                booking.IntakeFormReminderSentAt = now;
                sent++;
            }
        }

        await _context.SaveChangesAsync();
        _logger.LogInformation("Intake form reminders: {Sent}/{Total} sent across {TenantCount} tenant(s).",
            sent, candidates.Count, tenantIds.Count);
    }
}
