// Services/VoucherService.cs
// Pure balance/credit bookkeeping (Agency) — see Data/Entities/Voucher.cs. No payment processing;
// the tenant collects payment themselves and this only tracks remaining balance/sessions.
using GentleBook.Api.Configuration;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Services;

public class VoucherService
{
    private readonly GentleBookDbContext _context;
    private readonly EmailService _emailService;
    private readonly ILogger<VoucherService> _logger;

    public VoucherService(GentleBookDbContext context, EmailService emailService, ILogger<VoucherService> logger)
    {
        _context = context;
        _emailService = emailService;
        _logger = logger;
    }

    /// <summary>
    /// Called right after Customer.TotalBookings is incremented, from both the public
    /// (BookingService) and staff (ManualBookingService) booking-creation paths — one shared
    /// place for the "every Nth visit" reward rule so both stay consistent. No-ops silently if
    /// the feature is off, the tenant isn't Agency, or the threshold wasn't just crossed.
    /// </summary>
    public async Task MaybeIssueAutoRewardAsync(Guid tenantId, Customer customer)
    {
        var settings = await _context.TenantSettings.FirstOrDefaultAsync(s => s.TenantId == tenantId);
        if (settings == null || settings.LoyaltyRewardEveryNVisits <= 0) return;
        if (customer.TotalBookings % settings.LoyaltyRewardEveryNVisits != 0) return;

        var plan = await _context.Subscriptions
            .Where(s => s.TenantId == tenantId)
            .Select(s => (SubscriptionPlan?)s.Plan)
            .FirstOrDefaultAsync() ?? SubscriptionPlan.Trial;
        if (AgencyFeatureGate.ValidateNonAiForPlan(plan) != null) return;

        if (!Enum.TryParse<VoucherType>(settings.LoyaltyRewardType, out var type)) type = VoucherType.MonetaryValue;

        try
        {
            var voucher = await IssueAsync(
                tenantId, type, customer.Id,
                amount: type == VoucherType.MonetaryValue ? settings.LoyaltyRewardValue : null,
                sessions: type == VoucherType.SessionPackage && settings.LoyaltyRewardValue.HasValue
                    ? (int)Math.Round(settings.LoyaltyRewardValue.Value) : null,
                percentageValue: type == VoucherType.PercentageDiscount ? settings.LoyaltyRewardValue : null,
                expiresAt: null,
                note: $"Automatisch: Stammkunden-Belohnung nach {settings.LoyaltyRewardEveryNVisits} Besuchen",
                issuedByPlatformUserId: Guid.Empty);

            if (!string.IsNullOrWhiteSpace(customer.Email))
            {
                await _emailService.SendVoucherIssuedEmailAsync(
                    tenantId, customer.Email, customer.FullName, voucher.Code, type,
                    voucher.RemainingAmount, voucher.RemainingSessions, voucher.PercentageValue,
                    settings.CompanyName, settings.LogoUrl, settings.PrimaryColor);
            }

            _logger.LogInformation("Auto-issued loyalty reward voucher {Code} to customer {CustomerId} after {Visits} visits",
                voucher.Code, customer.Id, customer.TotalBookings);
        }
        catch (ArgumentException ex)
        {
            // LoyaltyRewardValue wasn't configured (e.g. 0/null) for the chosen type — skip
            // silently rather than breaking booking creation over a settings mistake.
            _logger.LogWarning("Skipped auto loyalty reward for tenant {TenantId}: {Message}", tenantId, ex.Message);
        }
    }

    public async Task<Voucher> IssueAsync(Guid tenantId, VoucherType type, Guid? customerId, decimal? amount, int? sessions, decimal? percentageValue, DateTime? expiresAt, string? note, Guid issuedByPlatformUserId)
    {
        if (type == VoucherType.MonetaryValue && (amount is null or <= 0))
            throw new ArgumentException("Bitte einen gültigen Geldwert angeben.");
        if (type == VoucherType.SessionPackage && (sessions is null or <= 0))
            throw new ArgumentException("Bitte eine gültige Anzahl Sitzungen angeben.");
        if (type == VoucherType.PercentageDiscount && (percentageValue is null or <= 0 or > 100))
            throw new ArgumentException("Bitte einen gültigen Prozentsatz (1-100) angeben.");

        // Percentage vouchers reuse InitialSessions/RemainingSessions as "uses remaining" — default
        // to a single-use coupon unless the caller specifies otherwise.
        var percentageUses = type == VoucherType.PercentageDiscount ? (sessions is > 0 ? sessions : 1) : null;

        var code = await GenerateUniqueCodeAsync(tenantId);
        var voucher = new Voucher
        {
            TenantId = tenantId,
            Code = code,
            CustomerId = customerId,
            Type = type,
            InitialAmount = type == VoucherType.MonetaryValue ? amount : null,
            RemainingAmount = type == VoucherType.MonetaryValue ? amount : null,
            InitialSessions = type == VoucherType.SessionPackage ? sessions : percentageUses,
            RemainingSessions = type == VoucherType.SessionPackage ? sessions : percentageUses,
            PercentageValue = type == VoucherType.PercentageDiscount ? percentageValue : null,
            ExpiresAt = expiresAt,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            IssuedByPlatformUserId = issuedByPlatformUserId,
        };

        _context.Vouchers.Add(voucher);
        await _context.SaveChangesAsync();
        return voucher;
    }

    /// <summary>Redeems a voucher against a booking's price: MonetaryValue deducts up to the remaining balance, SessionPackage/PercentageDiscount consume one use. Throws if the code is invalid, expired, cancelled, or already exhausted. Returns the voucher plus the computed discount amount (0 unless PercentageDiscount — purely informational, no payment is processed).</summary>
    public async Task<(Voucher voucher, decimal discountAmount)> RedeemAsync(Guid tenantId, string code, decimal bookingPrice)
    {
        var voucher = await _context.Vouchers
            .FirstOrDefaultAsync(v => v.TenantId == tenantId && v.Code == code.Trim().ToUpperInvariant());
        if (voucher == null)
            throw new ArgumentException("Gutschein-Code nicht gefunden.");
        if (voucher.Status != VoucherStatus.Active)
            throw new InvalidOperationException("Dieser Gutschein ist nicht mehr gültig.");
        if (voucher.ExpiresAt.HasValue && voucher.ExpiresAt.Value < DateTime.UtcNow)
        {
            voucher.Status = VoucherStatus.Expired;
            await _context.SaveChangesAsync();
            throw new InvalidOperationException("Dieser Gutschein ist abgelaufen.");
        }

        var discountAmount = 0m;

        if (voucher.Type == VoucherType.MonetaryValue)
        {
            if (voucher.RemainingAmount is not { } remaining || remaining <= 0)
                throw new InvalidOperationException("Dieser Gutschein hat kein Guthaben mehr.");
            discountAmount = Math.Min(remaining, bookingPrice);
            voucher.RemainingAmount = Math.Max(0, remaining - bookingPrice);
            if (voucher.RemainingAmount <= 0) voucher.Status = VoucherStatus.Redeemed;
        }
        else
        {
            if (voucher.RemainingSessions is not { } remainingSessions || remainingSessions <= 0)
                throw new InvalidOperationException(
                    voucher.Type == VoucherType.PercentageDiscount
                        ? "Dieser Gutschein wurde bereits verwendet."
                        : "Dieser Gutschein hat keine Sitzungen mehr.");
            voucher.RemainingSessions = remainingSessions - 1;
            if (voucher.RemainingSessions <= 0) voucher.Status = VoucherStatus.Redeemed;

            if (voucher.Type == VoucherType.PercentageDiscount && voucher.PercentageValue is { } pct)
                discountAmount = Math.Round(bookingPrice * pct / 100m, 2);
        }

        await _context.SaveChangesAsync();
        _logger.LogInformation("Voucher {Code} redeemed against booking price {Price}", voucher.Code, bookingPrice);
        return (voucher, discountAmount);
    }

    /// <summary>
    /// Restores the balance/session a booking consumed from its voucher, called when that
    /// booking is cancelled or deleted (found during live pre-launch testing: nothing did this
    /// before — a cancelled booking permanently kept the customer's voucher spent). Mutates the
    /// tracked Voucher/Booking entities only — does NOT call SaveChangesAsync itself, so the
    /// caller's own save persists this together with the booking's status change in one
    /// transaction, same atomicity guarantee RedeemAsync already has via ManualBookingService's
    /// transaction.
    /// </summary>
    public async Task RefundVoucherForBookingAsync(Booking booking)
    {
        if (string.IsNullOrWhiteSpace(booking.RedeemedVoucherCode) || booking.VoucherRefunded)
            return;

        var voucher = await _context.Vouchers
            .FirstOrDefaultAsync(v => v.TenantId == booking.TenantId && v.Code == booking.RedeemedVoucherCode);
        if (voucher == null)
        {
            _logger.LogWarning("Could not refund voucher {Code} for booking {BookingId}: voucher no longer exists", booking.RedeemedVoucherCode, booking.Id);
            booking.VoucherRefunded = true; // nothing left to refund against — don't keep retrying
            return;
        }

        // An admin explicitly cancelling the voucher itself is a deliberate "this is void" action
        // — a later, unrelated booking cancellation must not resurrect it.
        if (voucher.Status == VoucherStatus.Cancelled)
        {
            _logger.LogInformation("Skipped refund for booking {BookingId}: voucher {Code} was separately cancelled", booking.Id, voucher.Code);
            booking.VoucherRefunded = true;
            return;
        }

        if (voucher.Type == VoucherType.MonetaryValue)
        {
            if (booking.VoucherDiscountAmount is { } refundAmount && refundAmount > 0)
            {
                var cap = voucher.InitialAmount ?? decimal.MaxValue;
                voucher.RemainingAmount = Math.Min(cap, (voucher.RemainingAmount ?? 0) + refundAmount);
            }
        }
        else
        {
            var cap = voucher.InitialSessions ?? int.MaxValue;
            voucher.RemainingSessions = Math.Min(cap, (voucher.RemainingSessions ?? 0) + 1);
        }

        // Only reactivate a fully-spent voucher — never un-expire one past its ExpiresAt just
        // because a booking against it got cancelled.
        if (voucher.Status == VoucherStatus.Redeemed)
        {
            voucher.Status = (voucher.ExpiresAt.HasValue && voucher.ExpiresAt.Value < DateTime.UtcNow)
                ? VoucherStatus.Expired
                : VoucherStatus.Active;
        }

        booking.VoucherRefunded = true;
        _logger.LogInformation("Refunded voucher {Code} for cancelled/deleted booking {BookingId}", voucher.Code, booking.Id);
    }

    public async Task CancelAsync(Guid tenantId, Guid id)
    {
        var voucher = await _context.Vouchers.FirstOrDefaultAsync(v => v.Id == id && v.TenantId == tenantId)
            ?? throw new ArgumentException("Gutschein nicht gefunden.");
        voucher.Status = VoucherStatus.Cancelled;
        await _context.SaveChangesAsync();
    }

    private async Task<string> GenerateUniqueCodeAsync(Guid tenantId)
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O/1/I to avoid ambiguity
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var code = $"GB-{RandomSegment(4)}-{RandomSegment(4)}";
            var exists = await _context.Vouchers.AnyAsync(v => v.TenantId == tenantId && v.Code == code);
            if (!exists) return code;
        }
        throw new InvalidOperationException("Es konnte kein eindeutiger Gutschein-Code erzeugt werden. Bitte erneut versuchen.");

        string RandomSegment(int length) =>
            new(Enumerable.Range(0, length).Select(_ => chars[Random.Shared.Next(chars.Length)]).ToArray());
    }
}
