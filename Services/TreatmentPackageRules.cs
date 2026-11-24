using System.Security.Cryptography;
using GentleBook.Api.Configuration;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Services;

/// <summary>Derived balance of one package — see TreatmentPackage for why nothing here is stored.</summary>
public record PackageProgress(
    int TotalSessions,
    int UsedSessions,          // PreviouslyUsedSessions + Completed bookings
    int OpenSessions,          // max(0, Total - Used)
    int ReservedSessions)      // Pending/Confirmed bookings still linked (not yet completed, any date)
{
    /// <summary>How many more appointments may still be linked to this package.</summary>
    public int BookableSessions => Math.Max(0, OpenSessions - ReservedSessions);
    public bool IsUsedUp => OpenSessions == 0;
}

/// <summary>
/// Shared, stateless package rules used by the admin endpoints, the public booking flow, the
/// manual booking flow and the status-change flow. Static on purpose: these callers' constructors
/// (and every test factory building them) stay untouched.
/// </summary>
public static class TreatmentPackageRules
{
    public const int MaxSessions = 100;

    public static string NewBookingToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static async Task<SubscriptionPlan> GetPlanAsync(GentleBookDbContext db, Guid tenantId) =>
        await db.Subscriptions.IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId)
            .Select(s => (SubscriptionPlan?)s.Plan)
            .FirstOrDefaultAsync() ?? SubscriptionPlan.Trial;

    public static async Task<bool> IsEnabledForTenantAsync(GentleBookDbContext db, Guid tenantId) =>
        TreatmentPackageGate.IsAllowed(await GetPlanAsync(db, tenantId));

    public static async Task<Dictionary<Guid, PackageProgress>> GetProgressAsync(
        GentleBookDbContext db, IReadOnlyCollection<TreatmentPackage> packages, Guid? excludeBookingId = null)
    {
        var ids = packages.Select(p => p.Id).ToList();
        var counts = await db.Bookings.IgnoreQueryFilters()
            .Where(b => b.TreatmentPackageId != null && ids.Contains(b.TreatmentPackageId.Value)
                        && (excludeBookingId == null || b.Id != excludeBookingId))
            .GroupBy(b => b.TreatmentPackageId!.Value)
            .Select(g => new
            {
                PackageId = g.Key,
                Completed = g.Count(b => b.Status == BookingStatus.Completed),
                Reserved = g.Count(b => b.Status == BookingStatus.Pending || b.Status == BookingStatus.Confirmed),
            })
            .ToDictionaryAsync(x => x.PackageId);

        return packages.ToDictionary(p => p.Id, p =>
        {
            counts.TryGetValue(p.Id, out var c);
            var used = p.PreviouslyUsedSessions + (c?.Completed ?? 0);
            return new PackageProgress(p.TotalSessions, used, Math.Max(0, p.TotalSessions - used), c?.Reserved ?? 0);
        });
    }

    public static async Task<PackageProgress> GetProgressAsync(GentleBookDbContext db, TreatmentPackage package, Guid? excludeBookingId = null) =>
        (await GetProgressAsync(db, new[] { package }, excludeBookingId))[package.Id];

    /// <summary>
    /// Whether an appointment for (customer, service) may be linked to this package. Returns a
    /// German, user-facing reason when not. <paramref name="excludeBookingId"/> is the booking
    /// being (re-)assigned, so it doesn't count against its own capacity.
    /// </summary>
    public static async Task<string?> ValidateAssignmentAsync(
        GentleBookDbContext db, TreatmentPackage package, Guid customerId, Guid serviceId, Guid? excludeBookingId)
    {
        if (package.Status != TreatmentPackageStatus.Active)
            return "Dieses Behandlungspaket ist beendet.";
        if (package.CustomerId != customerId)
            return "Das Behandlungspaket gehört zu einer anderen Kundin bzw. einem anderen Kunden.";
        if (package.ServiceId != serviceId)
            return "Das Behandlungspaket gilt für eine andere Leistung.";

        var progress = await GetProgressAsync(db, package, excludeBookingId);
        if (progress.IsUsedUp)
            return "Das Behandlungspaket ist bereits vollständig verbraucht.";
        if (progress.BookableSessions <= 0)
            return "Für alle offenen Sitzungen dieses Pakets sind bereits Termine gebucht.";
        return null;
    }

    public static async Task<DateTime> GetTenantLocalNowAsync(GentleBookDbContext db, Guid tenantId)
    {
        var tzId = await db.TenantSettings.IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId).Select(s => s.TimeZone).FirstOrDefaultAsync() ?? "Europe/Berlin";
        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById(tzId); }
        catch { tz = TimeZoneInfo.Utc; }
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
    }
}
