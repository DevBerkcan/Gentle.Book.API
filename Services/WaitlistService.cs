using System.Data;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Services;

public class WaitlistService
{
    public const int ReservationMinutes = 15;

    private readonly GentleBookDbContext _db;
    private readonly EmailService _emailService;
    private readonly IBackgroundJobClient _jobs;

    public WaitlistService(
        GentleBookDbContext db,
        EmailService emailService,
        IBackgroundJobClient jobs)
    {
        _db = db;
        _emailService = emailService;
        _jobs = jobs;
    }

    public async Task NotifyNextForFreedSlotAsync(
        Guid tenantId,
        Guid serviceId,
        Guid? employeeId,
        DateOnly date,
        TimeOnly startTime,
        TimeOnly endTime)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var now = DateTime.UtcNow;

        var alreadyReserved = await _db.WaitlistEntries.AnyAsync(w =>
            w.TenantId == tenantId &&
            w.ServiceId == serviceId &&
            w.PreferredDate == date &&
            w.Status == WaitlistStatus.Notified &&
            w.ReservationExpiresAt > now &&
            w.ReservedStartTime == startTime &&
            w.ReservedEndTime == endTime &&
            w.ReservedEmployeeId == employeeId);

        if (alreadyReserved)
        {
            await tx.CommitAsync();
            return;
        }

        var entry = await _db.WaitlistEntries
            .Include(w => w.Service)
            .Where(w =>
                w.TenantId == tenantId &&
                w.ServiceId == serviceId &&
                w.PreferredDate == date &&
                w.Status == WaitlistStatus.Pending &&
                (w.EmployeeId == null || w.EmployeeId == employeeId) &&
                (w.PreferredStartTime == null ||
                    (startTime >= w.PreferredStartTime &&
                     (w.PreferredEndTime == null || endTime <= w.PreferredEndTime))))
            .OrderBy(w => w.CreatedAt)
            .FirstOrDefaultAsync();

        if (entry == null)
        {
            await tx.CommitAsync();
            return;
        }

        entry.Status = WaitlistStatus.Notified;
        entry.NotifiedAt = now;
        entry.ReservationToken = $"{Guid.NewGuid():N}{Guid.NewGuid():N}";
        entry.ReservationExpiresAt = now.AddMinutes(ReservationMinutes);
        entry.ReservedStartTime = startTime;
        entry.ReservedEndTime = endTime;
        entry.ReservedEmployeeId = employeeId;
        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        var tenantSettings = await _db.TenantSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId);
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId);

        var tenantName = tenantSettings?.CompanyName ?? tenant?.Name ?? "Buchungssystem";
        var tenantSlug = tenant?.Slug ?? "";
        var logoUrl = _emailService.GetAbsoluteLogoUrl(tenantSettings?.LogoUrl);
        var color = tenantSettings?.PrimaryColor ?? "#6355E4";
        var serviceName = entry.Service?.Name ?? "Ihrem Service";

        var sent = await _emailService.SendWaitlistNotificationAsync(
            entry.Email,
            entry.FirstName,
            entry.LastName,
            serviceName,
            date,
            startTime,
            endTime,
            tenantSlug,
            tenantName,
            logoUrl,
            color,
            entry.ReservationToken,
            serviceId,
            employeeId);

        if (!sent)
        {
            entry.ReservationExpiresAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            await ExpireReservationAsync(entry.Id, entry.ReservationToken);
            return;
        }

        _jobs.Schedule<WaitlistService>(
            service => service.ExpireReservationAsync(entry.Id, entry.ReservationToken),
            TimeSpan.FromMinutes(ReservationMinutes));
    }

    public async Task ExpireReservationAsync(Guid entryId, string reservationToken)
    {
        var entry = await _db.WaitlistEntries.FirstOrDefaultAsync(w =>
            w.Id == entryId &&
            w.Status == WaitlistStatus.Notified &&
            w.ReservationToken == reservationToken);

        if (entry == null || entry.ReservationExpiresAt > DateTime.UtcNow)
            return;

        entry.Status = WaitlistStatus.Expired;
        await _db.SaveChangesAsync();

        if (entry.ServiceId.HasValue &&
            entry.PreferredDate.HasValue &&
            entry.ReservedStartTime.HasValue &&
            entry.ReservedEndTime.HasValue)
        {
            await NotifyNextForFreedSlotAsync(
                entry.TenantId,
                entry.ServiceId.Value,
                entry.ReservedEmployeeId,
                entry.PreferredDate.Value,
                entry.ReservedStartTime.Value,
                entry.ReservedEndTime.Value);
        }
    }
}
