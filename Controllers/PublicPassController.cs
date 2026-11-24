// Controllers/PublicPassController.cs
// The customer's personal Studio-Pass — public, identified only by the unguessable
// Customer.PassToken. Returns exactly what the customer needs on the go and nothing internal:
// first name only (no e-mail/phone/notes), her own upcoming appointments with "Termin verwalten",
// open package sessions with "Nächste Sitzung buchen", stamp card, her rewards and her
// "Freundin einladen" link. Everything is scoped to the token's own studio.
using GentleBook.Api.Configuration;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Controllers;

[ApiController]
[Route("api/pass")]
[AllowAnonymous]
public class PublicPassController : ControllerBase
{
    private readonly GentleBookDbContext _db;
    private readonly EmailService _emailService;

    public PublicPassController(GentleBookDbContext db, EmailService emailService)
    {
        _db = db;
        _emailService = emailService;
    }

    [HttpGet("{token}")]
    public async Task<IActionResult> GetPass(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 64) return NotFound();

        var customer = await _db.Customers.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.PassToken == token);
        if (customer == null) return NotFound();
        var tenantId = customer.TenantId;

        var tenant = await _db.Tenants.Include(t => t.Settings).Include(t => t.Subscription)
            .AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId);
        if (tenant == null || !tenant.IsActive) return NotFound();
        var settings = tenant.Settings;
        var plan = tenant.Subscription?.Plan ?? SubscriptionPlan.Trial;
        var canBook = tenant.Subscription?.IsAccessAllowed == true;

        var localNow = await TreatmentPackageRules.GetTenantLocalNowAsync(_db, tenantId);
        var today = DateOnly.FromDateTime(localNow);
        var nowTime = TimeOnly.FromDateTime(localNow);

        // ── Upcoming appointments ────────────────────────────────
        var upcoming = (await _db.Bookings.IgnoreQueryFilters()
                .Where(b => b.CustomerId == customer.Id && b.TenantId == tenantId
                            && (b.Status == BookingStatus.Pending || b.Status == BookingStatus.Confirmed)
                            && (b.BookingDate > today || (b.BookingDate == today && b.StartTime >= nowTime)))
                .OrderBy(b => b.BookingDate).ThenBy(b => b.StartTime)
                .Take(5)
                .Select(b => new { b.Id, b.BookingDate, b.StartTime, b.EndTime, Service = b.Service.Name, Employee = b.Employee != null ? b.Employee.Name : null })
                .ToListAsync())
            .Select(b => new
            {
                date = b.BookingDate.ToString("yyyy-MM-dd"),
                startTime = b.StartTime.ToString("HH:mm"),
                endTime = b.EndTime.ToString("HH:mm"),
                serviceName = b.Service,
                employeeName = b.Employee,
                managePath = $"/booking/cancel/{_emailService.CreateCancellationToken(b.Id)}",
            })
            .ToList();

        // ── Treatment packages ───────────────────────────────────
        object[] packages = Array.Empty<object>();
        if (TreatmentPackageGate.IsAllowed(plan))
        {
            var active = await _db.TreatmentPackages.IgnoreQueryFilters()
                .Include(p => p.Service)
                .Where(p => p.CustomerId == customer.Id && p.TenantId == tenantId && p.Status == TreatmentPackageStatus.Active)
                .ToListAsync();
            var progress = await TreatmentPackageRules.GetProgressAsync(_db, active);
            packages = active.Where(p => !progress[p.Id].IsUsedUp).Select(p => (object)new
            {
                name = p.Name,
                serviceName = p.Service.Name,
                totalSessions = p.TotalSessions,
                usedSessions = progress[p.Id].UsedSessions,
                openSessions = progress[p.Id].OpenSessions,
                nextSessionBookingPath = canBook && progress[p.Id].BookableSessions > 0
                    ? $"/booking/{tenant.Slug}/book?serviceId={p.ServiceId}&packageToken={Uri.EscapeDataString(p.BookingToken)}"
                    : null,
            }).ToArray();
        }

        // ── Stamp card (Stammkunden-Belohnung) ───────────────────
        var visits = await VoucherService.CountCompletedVisitsAsync(_db, customer.Id);
        object? stamps = null;
        if (settings != null && settings.LoyaltyRewardEveryNVisits > 0 && AgencyFeatureGate.ValidateNonAiForPlan(plan) == null)
        {
            var n = settings.LoyaltyRewardEveryNVisits;
            stamps = new
            {
                target = n,
                current = visits % n,
                completedVisits = visits,
                rewardLabel = StampRewardLabel(settings),
            };
        }

        // ── Rewards: referral bonuses + active vouchers ──────────
        var referrals = await _db.Referrals.IgnoreQueryFilters()
            .Where(r => r.TenantId == tenantId && (r.ReferrerCustomerId == customer.Id || r.ReferredCustomerId == customer.Id))
            .ToListAsync();
        var rewards = new List<object>();
        foreach (var r in referrals.Where(r => r.Status == ReferralStatus.Qualified).OrderByDescending(r => r.QualifiedAt))
        {
            var asReferrer = r.ReferrerCustomerId == customer.Id;
            rewards.Add(new
            {
                kind = asReferrer ? "referral_thanks" : "referral_welcome",
                text = r.RewardText ?? StudioPassRules.DefaultRewardText,
                redeemed = (asReferrer ? r.ReferrerRewardRedeemedAt : r.ReferredRewardRedeemedAt) != null,
            });
        }
        var vouchers = await _db.Vouchers.IgnoreQueryFilters()
            .Where(v => v.CustomerId == customer.Id && v.TenantId == tenantId && v.Status == VoucherStatus.Active
                        && (v.ExpiresAt == null || v.ExpiresAt > DateTime.UtcNow))
            .OrderByDescending(v => v.IssuedAt)
            .Select(v => new { v.Code, Type = v.Type.ToString(), v.RemainingAmount, v.RemainingSessions, v.PercentageValue, v.ExpiresAt })
            .ToListAsync();

        // ── Freunde machen Freude ────────────────────────────────
        object? referral = null;
        if (canBook && await StudioPassRules.IsReferralActiveAsync(_db, tenantId))
        {
            var code = await StudioPassRules.EnsureReferralCodePersistedAsync(_db, customer.Id);
            referral = new
            {
                // Only after a completed visit ("nach einem abgeschlossenen Termin").
                canInvite = visits > 0,
                sharePath = $"/booking/{tenant.Slug}/book?ref={code}",
                rewardText = string.IsNullOrWhiteSpace(settings?.ReferralRewardText) ? StudioPassRules.DefaultRewardText : settings!.ReferralRewardText!.Trim(),
                invitedCount = referrals.Count(r => r.ReferrerCustomerId == customer.Id),
                firstVisitsCount = referrals.Count(r => r.ReferrerCustomerId == customer.Id && r.Status == ReferralStatus.Qualified),
            };
        }

        return Ok(new
        {
            studio = new
            {
                name = settings?.CompanyName ?? tenant.Name,
                slug = tenant.Slug,
                logoUrl = settings?.LogoUrl,
                primaryColor = settings?.PrimaryColor ?? "#6355E4",
                address = settings?.Address,
                phone = settings?.Phone,
                bookingPath = canBook ? $"/booking/{tenant.Slug}/book" : null,
            },
            customer = new { firstName = customer.FirstName },
            upcoming,
            packages,
            stamps,
            rewards,
            vouchers,
            referral,
        });
    }

    private static string StampRewardLabel(TenantSettings s)
    {
        var value = s.LoyaltyRewardValue ?? 0;
        return s.LoyaltyRewardType switch
        {
            "PercentageDiscount" => $"{value:0.##} % Rabatt",
            "SessionPackage" => (int)Math.Round(value) == 1 ? "1 Gratis-Behandlung" : $"{(int)Math.Round(value)} Gratis-Behandlungen",
            _ => $"{value:0.##} {(s.DefaultCurrency == "CHF" ? "CHF" : "€")} Gutschein",
        };
    }
}
