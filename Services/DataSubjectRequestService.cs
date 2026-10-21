using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Services;

// GDPR Art. 15 (export) / Art. 17 (erasure) on-demand self-service (LEGAL_AUDIT_2026-09-06.md
// HOCH: "Ein Supportlink ist kein umgesetzter Datenexport. | SubscriptionService,
// TenantMiddleware, Subscription-Seite"). Export is read-only and resolves immediately.
// Deletion is irreversible, so it only ever creates a Pending DataSubjectRequest here — the
// actual purge is a separate, SuperAdmin-reviewed step (see ApproveDeletionRequestAsync).
public class DataSubjectRequestService
{
    private readonly GentleBookDbContext _db;
    private readonly SubscriptionService _subscriptionService;
    private readonly EmailService _emailService;
    private readonly AuditService _audit;
    private readonly ILogger<DataSubjectRequestService> _logger;

    public DataSubjectRequestService(
        GentleBookDbContext db,
        SubscriptionService subscriptionService,
        EmailService emailService,
        AuditService audit,
        ILogger<DataSubjectRequestService> logger)
    {
        _db = db;
        _subscriptionService = subscriptionService;
        _emailService = emailService;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>Builds a complete, portable snapshot of everything the tenant stores operationally (GDPR Art. 15/20).</summary>
    public async Task<object> BuildTenantDataExportAsync(Guid tenantId)
    {
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId)
            ?? throw new InvalidOperationException("Tenant not found.");
        var settings = await _db.TenantSettings.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId);

        var admins = await _db.PlatformUsers.AsNoTracking()
            .Where(u => u.TenantId == tenantId)
            .Select(u => new { u.Id, u.Email, u.FirstName, u.LastName, Role = u.Role.ToString(), u.IsActive, u.LastLoginAt, u.CreatedAt })
            .ToListAsync();

        var employees = await _db.Employees.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.TenantId == tenantId)
            .Select(e => new { e.Id, e.Name, e.Role, e.Specialty, e.Username, e.Tagline, e.IsActive, e.CreatedAt })
            .ToListAsync();

        var locations = await _db.BusinessLocations.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.TenantId == tenantId)
            .ToListAsync();

        var services = await _db.Services.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .Select(s => new { s.Id, s.Name, s.Description, s.DurationMinutes, s.Price, s.Currency, s.IsActive })
            .ToListAsync();

        var customers = await _db.Customers.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .Select(c => new
            {
                c.Id, c.FirstName, c.LastName, c.Email, c.Phone, c.Notes,
                c.CreatedAt, c.LastVisit, c.TotalBookings, c.NoShowCount, c.ConsentGivenAt, c.LoyaltyPoints,
            })
            .ToListAsync();

        var bookings = await _db.Bookings.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.TenantId == tenantId)
            .Select(b => new
            {
                b.Id, b.CustomerId, b.ServiceId, b.EmployeeId, b.LocationId,
                b.BookingDate, b.StartTime, b.EndTime, Status = b.Status.ToString(),
                b.CustomerNotes, b.AdminNotes, b.CreatedAt, b.CancelledAt, b.CancellationReason,
            })
            .ToListAsync();

        var employeeNotes = await _db.EmployeeNotes.IgnoreQueryFilters().AsNoTracking()
            .Where(n => n.TenantId == tenantId)
            .ToListAsync();

        return new
        {
            ExportedAt = DateTime.UtcNow,
            Tenant = new { tenant.Id, tenant.Name, tenant.Slug, tenant.CreatedAt },
            Settings = settings == null ? null : new
            {
                settings.CompanyName, settings.LegalCompanyName, settings.Email, settings.Phone,
                settings.Address, settings.Website, settings.VatId,
                settings.BillingStreet, settings.BillingZipCode, settings.BillingCity, settings.BillingCountry,
            },
            Admins = admins,
            Employees = employees,
            Locations = locations,
            Services = services,
            Customers = customers,
            Bookings = bookings,
            EmployeeNotes = employeeNotes,
        };
    }

    /// <summary>Records that an export happened (Art. 15 requests resolve synchronously — there is nothing left pending).</summary>
    public async Task RecordExportAsync(Guid tenantId, Guid actorUserId)
    {
        _db.DataSubjectRequests.Add(new DataSubjectRequest
        {
            TenantId = tenantId,
            Type = DataSubjectRequestType.Export,
            Status = DataSubjectRequestStatus.Completed,
            RequestedByUserId = actorUserId,
            ProcessedByUserId = actorUserId,
            ProcessedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        await _audit.LogAsync("tenant.data_export_requested", "Tenant", tenantId.ToString(),
            "TenantAdmin hat einen vollständigen Datenexport (Art. 15 DSGVO) angefordert und erhalten.", tenantId);
    }

    /// <summary>
    /// Creates a Pending deletion request. Refuses while the subscription is still being paid for
    /// (erasure would otherwise destroy the operational data a live, billed service depends on —
    /// the tenant must cancel first, exactly like the existing 30-day automated purge already
    /// only runs after cancellation) and while another request is already pending.
    /// </summary>
    public async Task<(bool Success, string? Error, DataSubjectRequest? Request)> RequestDeletionAsync(Guid tenantId, Guid actorUserId)
    {
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.TenantId == tenantId);
        if (sub == null)
            return (false, "Kein Abonnement gefunden.", null);
        if (sub.Status is not (SubscriptionStatus.Cancelled or SubscriptionStatus.Expired))
            return (false, "Bitte kündigen Sie zuerst Ihr Abonnement, bevor Sie die Löschung Ihrer Daten beantragen.", null);

        var alreadyPending = await _db.DataSubjectRequests.AnyAsync(r =>
            r.TenantId == tenantId && r.Type == DataSubjectRequestType.Deletion && r.Status == DataSubjectRequestStatus.Pending);
        if (alreadyPending)
            return (false, "Es liegt bereits eine offene Löschanfrage vor.", null);

        var request = new DataSubjectRequest
        {
            TenantId = tenantId,
            Type = DataSubjectRequestType.Deletion,
            Status = DataSubjectRequestStatus.Pending,
            RequestedByUserId = actorUserId,
        };
        _db.DataSubjectRequests.Add(request);
        await _db.SaveChangesAsync();

        await _audit.LogAsync("tenant.deletion_requested", "Tenant", tenantId.ToString(),
            "TenantAdmin hat die Löschung aller operativen Daten (Art. 17 DSGVO) beantragt.", tenantId);

        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId);
        var requester = await _db.PlatformUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == actorUserId);
        if (tenant != null)
        {
            await _emailService.SendDataDeletionRequestNotificationAsync(
                tenant.Name, tenant.Slug, requester?.Email ?? "unbekannt");
        }

        return (true, null, request);
    }

    public async Task<List<DataSubjectRequest>> GetRequestsForTenantAsync(Guid tenantId) =>
        await _db.DataSubjectRequests.AsNoTracking()
            .Where(r => r.TenantId == tenantId)
            .OrderByDescending(r => r.RequestedAt)
            .ToListAsync();

    public async Task<List<DataSubjectRequest>> GetAllRequestsAsync(DataSubjectRequestStatus? status = null)
    {
        var query = _db.DataSubjectRequests.AsNoTracking();
        if (status.HasValue) query = query.Where(r => r.Status == status.Value);
        return await query.OrderByDescending(r => r.RequestedAt).ToListAsync();
    }

    /// <summary>SuperAdmin approval: runs the same irreversible purge the 30-day retention job would run, but now.</summary>
    public async Task<(bool Success, string? Error)> ApproveDeletionRequestAsync(Guid requestId, Guid approverUserId)
    {
        var request = await _db.DataSubjectRequests.FirstOrDefaultAsync(r => r.Id == requestId);
        if (request == null) return (false, "Anfrage nicht gefunden.");
        if (request.Type != DataSubjectRequestType.Deletion) return (false, "Keine Löschanfrage.");
        if (request.Status != DataSubjectRequestStatus.Pending) return (false, "Anfrage wurde bereits bearbeitet.");

        // Capture the admin's email before the purge removes the PlatformUsers row.
        var admin = await _db.PlatformUsers.AsNoTracking()
            .Where(u => u.TenantId == request.TenantId && u.Role == PlatformRole.TenantAdmin && u.IsActive)
            .OrderBy(u => u.CreatedAt)
            .FirstOrDefaultAsync();

        var purged = await _subscriptionService.PurgeTenantDataOnDemandAsync(request.TenantId);
        if (!purged) return (false, "Daten wurden bereits gelöscht oder es existiert kein Abonnement.");

        request.Status = DataSubjectRequestStatus.Completed;
        request.ProcessedByUserId = approverUserId;
        request.ProcessedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await _audit.LogAsync("tenant.deletion_request_approved", "Tenant", request.TenantId.ToString(),
            "Löschanfrage (Art. 17 DSGVO) geprüft und umgesetzt.", request.TenantId);

        if (admin != null)
            await _emailService.SendDataDeletionRequestResolvedAsync(admin.Email, admin.FirstName, approved: true, reason: null);

        return (true, null);
    }

    /// <summary>
    /// SuperAdmin-direct purge, with no TenantAdmin-initiated DataSubjectRequest needed — for
    /// administrative test-data cleanup where the SuperAdmin IS the one deciding, not reviewing
    /// someone else's request. Runs the exact same irreversible anonymizing purge as the
    /// reviewed flow (PurgeTenantDataOnDemandAsync keeps Invoices and the Tenant shell row for
    /// statutory/FK reasons — this is not a hard tenant delete).
    /// </summary>
    public async Task<(bool Success, string? Error)> SuperAdminPurgeAsync(Guid tenantId, Guid actorUserId, bool force = false)
    {
        var purged = await _subscriptionService.PurgeTenantDataOnDemandAsync(tenantId, force);
        if (!purged)
            return (false, "Daten wurden bereits gelöscht oder es existiert kein Abonnement.");

        await _audit.LogAsync("tenant.operational_data_purged_by_superadmin", "Tenant", tenantId.ToString(),
            "SuperAdmin hat die operativen Daten direkt bereinigt (kein kundeninitiierter Antrag).", tenantId);

        return (true, null);
    }

    public async Task<(bool Success, string? Error)> RejectDeletionRequestAsync(Guid requestId, Guid approverUserId, string reason)
    {
        var request = await _db.DataSubjectRequests.FirstOrDefaultAsync(r => r.Id == requestId);
        if (request == null) return (false, "Anfrage nicht gefunden.");
        if (request.Type != DataSubjectRequestType.Deletion) return (false, "Keine Löschanfrage.");
        if (request.Status != DataSubjectRequestStatus.Pending) return (false, "Anfrage wurde bereits bearbeitet.");

        request.Status = DataSubjectRequestStatus.Rejected;
        request.ProcessedByUserId = approverUserId;
        request.ProcessedAt = DateTime.UtcNow;
        request.Notes = reason;
        await _db.SaveChangesAsync();

        await _audit.LogAsync("tenant.deletion_request_rejected", "Tenant", request.TenantId.ToString(),
            $"Löschanfrage abgelehnt: {reason}", request.TenantId);

        var admin = await _db.PlatformUsers.AsNoTracking()
            .Where(u => u.TenantId == request.TenantId && u.Role == PlatformRole.TenantAdmin && u.IsActive)
            .OrderBy(u => u.CreatedAt)
            .FirstOrDefaultAsync();
        if (admin != null)
            await _emailService.SendDataDeletionRequestResolvedAsync(admin.Email, admin.FirstName, approved: false, reason: reason);

        return (true, null);
    }
}
