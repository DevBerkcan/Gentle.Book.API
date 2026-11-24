using System.Security.Cryptography;
using GentleBook.Api.Configuration;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Services;

/// <summary>
/// Studio-Pass tokens and "Freunde machen Freude" rules. Static and DbContext-only on purpose, like
/// TreatmentPackageRules: the booking/completion services that call into this keep their
/// constructors (and every test factory building them) unchanged.
/// </summary>
public static class StudioPassRules
{
    // No 0/O/1/I/L — the code gets read aloud and typed from WhatsApp screenshots.
    private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    public const string DefaultRewardText = "Eine kleine Überraschung beim nächsten Besuch";

    public static string NewPassToken() => TreatmentPackageRules.NewBookingToken();

    private static string NewReferralCode()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        var chars = new char[8];
        for (var i = 0; i < 8; i++) chars[i] = CodeAlphabet[bytes[i] % CodeAlphabet.Length];
        return new string(chars);
    }

    /// <summary>Creates the customer's pass token if missing. Caller saves. In-memory only — production paths use <see cref="EnsurePassTokenPersistedAsync"/>.</summary>
    public static string EnsurePassToken(Customer customer)
    {
        if (string.IsNullOrEmpty(customer.PassToken)) customer.PassToken = NewPassToken();
        return customer.PassToken;
    }

    /// <summary>
    /// Race-safe "get or create" for the pass token, persisted immediately. Found in E2E testing:
    /// the backgrounded confirmation e-mail job and the Kundenkartei could both lazily create a
    /// token for the same new customer — last writer won, so a link already handed out (e-mail or
    /// copied by the studio) silently stopped working. A conditional UPDATE ... WHERE PassToken IS
    /// NULL makes the first writer win; everyone then reads back that one persisted token.
    /// </summary>
    public static Task<string> EnsurePassTokenPersistedAsync(GentleBookDbContext db, Guid customerId) =>
        EnsurePersistedAsync(db, customerId, NewPassToken(),
            c => c.PassToken,
            q => q.Where(c => c.PassToken == null),
            (q, v) => q.ExecuteUpdateAsync(s => s.SetProperty(c => c.PassToken, v)),
            (c, v) => c.PassToken = v);

    /// <summary>Race-safe "get or create" for the public referral code (same reasoning as the pass token).</summary>
    public static async Task<string> EnsureReferralCodePersistedAsync(GentleBookDbContext db, Guid customerId)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var candidate = NewReferralCode();
            if (await db.Customers.IgnoreQueryFilters().AnyAsync(c => c.ReferralCode == candidate)) continue;
            return await EnsurePersistedAsync(db, customerId, candidate,
                c => c.ReferralCode,
                q => q.Where(c => c.ReferralCode == null),
                (q, v) => q.ExecuteUpdateAsync(s => s.SetProperty(c => c.ReferralCode, v)),
                (c, v) => c.ReferralCode = v);
        }
        throw new InvalidOperationException("Could not allocate a unique referral code.");
    }

    private static async Task<string> EnsurePersistedAsync(
        GentleBookDbContext db, Guid customerId, string candidate,
        Func<Customer, string?> get,
        Func<IQueryable<Customer>, IQueryable<Customer>> whereEmpty,
        Func<IQueryable<Customer>, string, Task<int>> conditionalUpdate,
        Action<Customer, string> set)
    {
        var tracked = db.Customers.Local.FirstOrDefault(c => c.Id == customerId);
        if (tracked != null && get(tracked) is { Length: > 0 } existing) return existing;

        if (db.Database.IsRelational())
        {
            await conditionalUpdate(whereEmpty(db.Customers.IgnoreQueryFilters().Where(c => c.Id == customerId)), candidate);
            var persisted = await db.Customers.IgnoreQueryFilters().AsNoTracking()
                .Where(c => c.Id == customerId).Select(c => get(c)).FirstAsync();
            if (tracked != null)
            {
                set(tracked, persisted!);
                // Already in the DB — keep EF from writing it again with a stale original value.
                var entry = db.Entry(tracked);
                foreach (var prop in entry.Properties.Where(p => p.Metadata.Name is nameof(Customer.PassToken) or nameof(Customer.ReferralCode)))
                    if (Equals(prop.CurrentValue, persisted)) { prop.OriginalValue = persisted; prop.IsModified = false; }
            }
            return persisted!;
        }

        // EF InMemory (tests): no ExecuteUpdate and no real concurrency — plain get-or-set.
        var customer = tracked ?? await db.Customers.IgnoreQueryFilters().FirstAsync(c => c.Id == customerId);
        if (get(customer) is { Length: > 0 } current) return current;
        set(customer, candidate);
        await db.SaveChangesAsync();
        return candidate;
    }

    /// <summary>Creates a unique referral code if missing. Caller saves.</summary>
    public static async Task<string> EnsureReferralCodeAsync(GentleBookDbContext db, Customer customer)
    {
        if (!string.IsNullOrEmpty(customer.ReferralCode)) return customer.ReferralCode;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var code = NewReferralCode();
            if (!await db.Customers.IgnoreQueryFilters().AnyAsync(c => c.ReferralCode == code))
            {
                customer.ReferralCode = code;
                return code;
            }
        }
        throw new InvalidOperationException("Could not allocate a unique referral code.");
    }

    public static async Task<bool> IsReferralActiveAsync(GentleBookDbContext db, Guid tenantId)
    {
        var plan = await TreatmentPackageRules.GetPlanAsync(db, tenantId);
        if (!ReferralGate.IsAllowed(plan)) return false;
        return await db.TenantSettings.IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId).Select(s => s.ReferralEnabled).FirstOrDefaultAsync();
    }

    private static string? Normalize(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().ToLowerInvariant();
    private static string? NormalizePhone(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : new string(s.Where(char.IsDigit).ToArray()) is { Length: > 0 } d ? d : null;

    /// <summary>
    /// Records a Pending referral for a NEW customer's first booking made via ?ref=CODE. Silently
    /// does nothing for anything that isn't a clean, first-time, third-party referral — a referral
    /// problem must never block a booking. Must be called BEFORE the new booking is saved
    /// (it checks the customer has no earlier bookings). Caller saves.
    /// </summary>
    public static async Task<Referral?> TryRecordReferralAsync(
        GentleBookDbContext db, Guid tenantId, string? referralCode, Customer customer, Booking booking)
    {
        var code = referralCode?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(code) || code.Length > 16) return null;
        if (!await IsReferralActiveAsync(db, tenantId)) return null;

        var referrer = await db.Customers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.ReferralCode == code && c.TenantId == tenantId);
        if (referrer == null || referrer.Id == customer.Id) return null;

        // Same person under a second record (shared e-mail/phone) is not a new customer.
        var e1 = Normalize(referrer.Email); var e2 = Normalize(customer.Email);
        var p1 = NormalizePhone(referrer.Phone); var p2 = NormalizePhone(customer.Phone);
        if ((e1 != null && e1 == e2) || (p1 != null && p1 == p2)) return null;

        // Only genuinely new customers count: no earlier booking in this studio, never referred before.
        if (await db.Bookings.IgnoreQueryFilters().AnyAsync(b => b.CustomerId == customer.Id && b.Id != booking.Id)) return null;
        if (await db.Referrals.IgnoreQueryFilters().AnyAsync(r => r.ReferredCustomerId == customer.Id)) return null;
        if (db.Referrals.Local.Any(r => r.ReferredCustomerId == customer.Id)) return null;

        var referral = new Referral
        {
            TenantId = tenantId,
            ReferrerCustomerId = referrer.Id,
            ReferredCustomerId = customer.Id,
            FirstBookingId = booking.Id,
        };
        db.Referrals.Add(referral);
        return referral;
    }

    /// <summary>
    /// Called whenever a booking becomes Completed (manual status change and the auto-complete
    /// job). Qualifies a Pending referral of that booking's customer exactly once and snapshots the
    /// studio's current reward. Saves itself. Idempotent: an already Qualified referral is untouched.
    /// </summary>
    public static async Task QualifyReferralOnCompletionAsync(GentleBookDbContext db, Booking booking)
    {
        var referral = await db.Referrals.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.ReferredCustomerId == booking.CustomerId
                                      && r.TenantId == booking.TenantId
                                      && r.Status == ReferralStatus.Pending);
        if (referral == null) return;

        var rewardText = await db.TenantSettings.IgnoreQueryFilters()
            .Where(s => s.TenantId == booking.TenantId).Select(s => s.ReferralRewardText).FirstOrDefaultAsync();

        referral.Status = ReferralStatus.Qualified;
        referral.QualifiedAt = DateTime.UtcNow;
        referral.QualifyingBookingId = booking.Id;
        referral.RewardText = string.IsNullOrWhiteSpace(rewardText) ? DefaultRewardText : rewardText.Trim();
        await db.SaveChangesAsync();
    }
}
