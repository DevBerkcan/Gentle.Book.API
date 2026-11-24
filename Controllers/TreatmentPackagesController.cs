// Controllers/TreatmentPackagesController.cs
// Behandlungspakete (Laser-/Kosmetikstudios): manage packages per customer, link appointments to
// a package, and the dashboard list "Offene Folgetermine".
//
// Authorization, applied to every endpoint:
//  - Tenant isolation: TreatmentPackage/Booking/Customer/Service all carry the global tenant query
//    filter (GentleBookDbContext), so ids from another studio simply resolve to "not found".
//  - Roles: TenantAdmin (incl. SuperAdmin impersonation) full access; LocationAdmin only for
//    packages whose service belongs to their location; Employees may only READ the package/notes
//    panel of their OWN bookings (GetForBooking). Everything else is admin-only.
//  - Plan: TreatmentPackageGate (Trial/Professional/Agency). The internal customer notes in the
//    booking panel are NOT plan-gated (available to every plan/industry).
using GentleBook.Api.Configuration;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Data;

namespace GentleBook.Api.Controllers;

[ApiController]
[Route("api/treatment-packages")]
[Authorize]
public class TreatmentPackagesController : ControllerBase
{
    private readonly GentleBookDbContext _db;
    private readonly ITenantContext _tenantContext;
    private readonly AuditService _audit;

    public TreatmentPackagesController(GentleBookDbContext db, ITenantContext tenantContext, AuditService audit)
    {
        _db = db;
        _tenantContext = tenantContext;
        _audit = audit;
    }

    // ── Access helpers ───────────────────────────────────────────

    private string? Role => JwtService.GetRole(User);
    private bool IsTenantWideAdmin => Role is "TenantAdmin" or "SuperAdmin";
    private bool IsLocationAdmin => Role == "LocationAdmin";
    private bool IsAnyAdmin => IsTenantWideAdmin || IsLocationAdmin;
    private Guid? LocationScope => IsLocationAdmin ? JwtService.GetLocationId(User) : null;

    private async Task<(Guid tenantId, IActionResult? deny)> RequireAdminWithPlanAsync()
    {
        if (_tenantContext.TenantId is not { } tenantId)
            return (Guid.Empty, Unauthorized(new { message = "Kein Tenant im Token" }));
        if (!IsAnyAdmin) return (tenantId, Forbid());
        if (IsLocationAdmin && LocationScope == null) return (tenantId, Forbid());

        var plan = await TreatmentPackageRules.GetPlanAsync(_db, tenantId);
        if (!TreatmentPackageGate.IsAllowed(plan))
        {
            return (tenantId, StatusCode(402, new
            {
                message = "Behandlungspakete sind in den Tarifen Pro und Agency enthalten.",
                feature = TreatmentPackageGate.FeatureKey,
                upgrade = true,
                currentPlan = PlanLimits.Get(plan).DisplayName,
                requiredPlan = PlanLimits.Get(SubscriptionPlan.Professional).DisplayName,
            }));
        }
        return (tenantId, null);
    }

    /// <summary>Packages visible to the caller — tenant-filtered by the DbContext, plus LocationAdmin scope.</summary>
    private IQueryable<TreatmentPackage> VisiblePackages()
    {
        var q = _db.TreatmentPackages.AsQueryable();
        if (LocationScope is { } locationId)
            q = q.Where(p => p.Service.LocationId == locationId);
        return q;
    }

    private async Task<string> GetTenantSlugAsync(Guid tenantId) =>
        await _db.Tenants.Where(t => t.Id == tenantId).Select(t => t.Slug).FirstAsync();

    private static string BookingLinkPath(string slug, TreatmentPackage p) =>
        $"/booking/{Uri.EscapeDataString(slug)}/book?serviceId={p.ServiceId}&packageToken={Uri.EscapeDataString(p.BookingToken)}";

    private static string StatusLabel(TreatmentPackage p, PackageProgress progress) =>
        p.Status == TreatmentPackageStatus.Cancelled ? "Cancelled" : progress.IsUsedUp ? "UsedUp" : "Active";

    private async Task<List<TreatmentPackageDto>> ToDtosAsync(Guid tenantId, List<TreatmentPackage> packages)
    {
        if (packages.Count == 0) return new();
        var progress = await TreatmentPackageRules.GetProgressAsync(_db, packages);
        var slug = await GetTenantSlugAsync(tenantId);
        var localNow = await TreatmentPackageRules.GetTenantLocalNowAsync(_db, tenantId);
        var today = DateOnly.FromDateTime(localNow);
        var nowTime = TimeOnly.FromDateTime(localNow);
        var ids = packages.Select(p => p.Id).ToList();

        var upcoming = (await _db.Bookings
                .Where(b => b.TreatmentPackageId != null && ids.Contains(b.TreatmentPackageId.Value)
                            && (b.Status == BookingStatus.Pending || b.Status == BookingStatus.Confirmed)
                            && (b.BookingDate > today || (b.BookingDate == today && b.StartTime >= nowTime)))
                .Select(b => new { PackageId = b.TreatmentPackageId!.Value, b.BookingDate, b.StartTime })
                .ToListAsync())
            .GroupBy(b => b.PackageId)
            .ToDictionary(g => g.Key, g => g.OrderBy(b => b.BookingDate).ThenBy(b => b.StartTime).First());

        return packages.Select(p =>
        {
            var pr = progress[p.Id];
            upcoming.TryGetValue(p.Id, out var next);
            return new TreatmentPackageDto(
                p.Id, p.CustomerId, p.Customer?.FullName ?? "", p.ServiceId, p.Service?.Name ?? "",
                p.Name, p.TotalSessions, p.PreviouslyUsedSessions, pr.UsedSessions, pr.OpenSessions,
                pr.ReservedSessions, pr.BookableSessions, StatusLabel(p, pr),
                next == null ? null : next.BookingDate.ToString("yyyy-MM-dd"),
                next == null ? null : next.StartTime.ToString("HH:mm"),
                BookingLinkPath(slug, p), p.CreatedAt);
        }).ToList();
    }

    // ── Packages of one customer (Kundenkartei) ──────────────────

    [HttpGet]
    public async Task<IActionResult> GetForCustomer([FromQuery] Guid customerId)
    {
        var (tenantId, deny) = await RequireAdminWithPlanAsync();
        if (deny != null) return deny;

        if (!await _db.Customers.AnyAsync(c => c.Id == customerId))
            return NotFound(new { message = "Kunde nicht gefunden" });

        var packages = await VisiblePackages()
            .Include(p => p.Customer).Include(p => p.Service)
            .Where(p => p.CustomerId == customerId)
            .OrderBy(p => p.Status).ThenByDescending(p => p.CreatedAt)
            .ToListAsync();

        return Ok(await ToDtosAsync(tenantId, packages));
    }

    /// <summary>One package plus the customer's contact data — used to prefill the manual booking form ("Termin buchen").</summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetOne(Guid id)
    {
        var (tenantId, deny) = await RequireAdminWithPlanAsync();
        if (deny != null) return deny;

        var package = await VisiblePackages().Include(p => p.Customer).Include(p => p.Service)
            .FirstOrDefaultAsync(p => p.Id == id);
        if (package == null) return NotFound(new { message = "Behandlungspaket nicht gefunden" });

        var dto = (await ToDtosAsync(tenantId, new() { package }))[0];
        return Ok(new
        {
            package = dto,
            customer = new { package.Customer.FirstName, package.Customer.LastName, package.Customer.Email, package.Customer.Phone },
        });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTreatmentPackageDto dto)
    {
        var (tenantId, deny) = await RequireAdminWithPlanAsync();
        if (deny != null) return deny;

        if (dto.TotalSessions < 1 || dto.TotalSessions > TreatmentPackageRules.MaxSessions)
            return BadRequest(new { message = $"Die Anzahl der Sitzungen muss zwischen 1 und {TreatmentPackageRules.MaxSessions} liegen." });
        var previouslyUsed = dto.PreviouslyUsedSessions ?? 0;
        if (previouslyUsed < 0 || previouslyUsed >= dto.TotalSessions)
            return BadRequest(new { message = "Bereits verbrauchte Sitzungen müssen kleiner als die Gesamtzahl sein." });

        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == dto.CustomerId);
        if (customer == null) return NotFound(new { message = "Kunde nicht gefunden" });

        var service = await _db.Services.FirstOrDefaultAsync(s => s.Id == dto.ServiceId && s.IsActive);
        if (service == null) return BadRequest(new { message = "Leistung nicht gefunden oder inaktiv." });
        if (LocationScope is { } loc && service.LocationId != loc) return Forbid();

        var name = string.IsNullOrWhiteSpace(dto.Name)
            ? $"{service.Name} – {dto.TotalSessions} Sitzungen"
            : dto.Name.Trim();
        if (name.Length > 200) return BadRequest(new { message = "Der Paketname darf höchstens 200 Zeichen lang sein." });

        var package = new TreatmentPackage
        {
            TenantId = tenantId,
            CustomerId = customer.Id,
            ServiceId = service.Id,
            Name = name,
            TotalSessions = dto.TotalSessions,
            PreviouslyUsedSessions = previouslyUsed,
            BookingToken = TreatmentPackageRules.NewBookingToken(),
            CreatedByUserId = JwtService.GetUserId(User),
        };
        _db.TreatmentPackages.Add(package);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("treatment_package.created", "TreatmentPackage", package.Id.ToString(),
            $"{name} für Kunde {customer.Id}", tenantId);

        package.Customer = customer;
        package.Service = service;
        return Ok((await ToDtosAsync(tenantId, new() { package }))[0]);
    }

    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateTreatmentPackageDto dto)
    {
        var (tenantId, deny) = await RequireAdminWithPlanAsync();
        if (deny != null) return deny;

        var package = await VisiblePackages().Include(p => p.Customer).Include(p => p.Service)
            .FirstOrDefaultAsync(p => p.Id == id);
        if (package == null) return NotFound(new { message = "Behandlungspaket nicht gefunden" });

        if (dto.Name != null)
        {
            var name = dto.Name.Trim();
            if (name.Length is 0 or > 200) return BadRequest(new { message = "Bitte einen Paketnamen mit höchstens 200 Zeichen angeben." });
            package.Name = name;
        }

        var newTotal = dto.TotalSessions ?? package.TotalSessions;
        var newPrevious = dto.PreviouslyUsedSessions ?? package.PreviouslyUsedSessions;
        if (newTotal < 1 || newTotal > TreatmentPackageRules.MaxSessions)
            return BadRequest(new { message = $"Die Anzahl der Sitzungen muss zwischen 1 und {TreatmentPackageRules.MaxSessions} liegen." });
        if (newPrevious < 0)
            return BadRequest(new { message = "Bereits verbrauchte Sitzungen dürfen nicht negativ sein." });

        var current = await TreatmentPackageRules.GetProgressAsync(_db, package);
        var completedInGentleBook = current.UsedSessions - package.PreviouslyUsedSessions;
        if (newPrevious + completedInGentleBook + current.ReservedSessions > newTotal)
            return BadRequest(new
            {
                message = $"Das Paket hat bereits {completedInGentleBook} abgeschlossene und {current.ReservedSessions} gebuchte Sitzung(en). " +
                          "Die Gesamtzahl darf nicht kleiner sein als verbrauchte plus gebuchte Sitzungen.",
            });
        package.TotalSessions = newTotal;
        package.PreviouslyUsedSessions = newPrevious;

        if (dto.Status != null)
        {
            if (!Enum.TryParse<TreatmentPackageStatus>(dto.Status, true, out var status))
                return BadRequest(new { message = "Status muss Active oder Cancelled sein." });
            package.Status = status;
        }

        package.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.LogAsync("treatment_package.updated", "TreatmentPackage", package.Id.ToString(),
            $"{package.Name}: {package.TotalSessions} Sitzungen, Vorab {package.PreviouslyUsedSessions}, {package.Status}", tenantId);

        return Ok((await ToDtosAsync(tenantId, new() { package }))[0]);
    }

    /// <summary>Invalidates the old personal booking link and issues a new one.</summary>
    [HttpPost("{id:guid}/booking-link/renew")]
    public async Task<IActionResult> RenewBookingLink(Guid id)
    {
        var (tenantId, deny) = await RequireAdminWithPlanAsync();
        if (deny != null) return deny;

        var package = await VisiblePackages().Include(p => p.Customer).Include(p => p.Service)
            .FirstOrDefaultAsync(p => p.Id == id);
        if (package == null) return NotFound(new { message = "Behandlungspaket nicht gefunden" });

        package.BookingToken = TreatmentPackageRules.NewBookingToken();
        package.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.LogAsync("treatment_package.link_renewed", "TreatmentPackage", package.Id.ToString(), package.Name, tenantId);

        return Ok((await ToDtosAsync(tenantId, new() { package }))[0]);
    }

    // ── Dashboard: Offene Folgetermine ───────────────────────────

    [HttpGet("follow-ups")]
    public async Task<IActionResult> GetFollowUps()
    {
        var (tenantId, deny) = await RequireAdminWithPlanAsync();
        if (deny != null) return deny;

        var packages = await VisiblePackages()
            .Include(p => p.Customer).Include(p => p.Service)
            .Where(p => p.Status == TreatmentPackageStatus.Active)
            .ToListAsync();
        if (packages.Count == 0) return Ok(new List<FollowUpDto>());

        var progress = await TreatmentPackageRules.GetProgressAsync(_db, packages);
        var open = packages.Where(p => !progress[p.Id].IsUsedUp).ToList();
        if (open.Count == 0) return Ok(new List<FollowUpDto>());

        var ids = open.Select(p => p.Id).ToList();
        var bookings = await _db.Bookings
            .Where(b => b.TreatmentPackageId != null && ids.Contains(b.TreatmentPackageId.Value))
            .Select(b => new { PackageId = b.TreatmentPackageId!.Value, b.BookingDate, b.StartTime, b.Status })
            .ToListAsync();
        var byPackage = bookings.ToLookup(b => b.PackageId);

        var localNow = await TreatmentPackageRules.GetTenantLocalNowAsync(_db, tenantId);
        var today = DateOnly.FromDateTime(localNow);
        var nowTime = TimeOnly.FromDateTime(localNow);
        bool IsFuture(DateOnly d, TimeOnly t) => d > today || (d == today && t >= nowTime);

        var slug = await GetTenantSlugAsync(tenantId);
        var result = new List<FollowUpDto>();
        foreach (var p in open)
        {
            var list = byPackage[p.Id].ToList();
            var hasValidUpcoming = list.Any(b =>
                (b.Status == BookingStatus.Pending || b.Status == BookingStatus.Confirmed) && IsFuture(b.BookingDate, b.StartTime));
            if (hasValidUpcoming) continue;

            var last = list.OrderByDescending(b => b.BookingDate).ThenByDescending(b => b.StartTime).FirstOrDefault();
            var (code, label) = last?.Status switch
            {
                null => ("no_appointment_yet", "Folgetermin fehlt"),
                BookingStatus.Cancelled => ("cancelled_without_replacement", "Absage ohne Ersatztermin"),
                BookingStatus.NoShow => ("no_show_without_replacement", "Nicht erschienen, kein neuer Termin"),
                BookingStatus.Pending or BookingStatus.Confirmed => ("last_not_completed", "Letzter Termin noch nicht abgeschlossen"),
                _ => ("missing_follow_up", "Folgetermin fehlt"),
            };

            var pr = progress[p.Id];
            result.Add(new FollowUpDto(
                p.Id, p.Name, p.CustomerId, p.Customer.FullName, p.Customer.Phone, p.Customer.Email,
                p.ServiceId, p.Service.Name, pr.TotalSessions, pr.UsedSessions, pr.OpenSessions,
                code, label,
                last == null ? null : last.BookingDate.ToString("yyyy-MM-dd"),
                BookingLinkPath(slug, p)));
        }

        // Longest-waiting first: never-booked packages, then by oldest last appointment.
        return Ok(result
            .OrderBy(r => r.LastAppointmentDate == null ? 0 : 1)
            .ThenBy(r => r.LastAppointmentDate)
            .ThenBy(r => r.CustomerName)
            .ToList());
    }

    // ── Booking view: package + internal customer notes ──────────

    private async Task<(Booking? booking, IActionResult? deny)> LoadAccessibleBookingAsync(Guid bookingId, bool adminOnly)
    {
        if (_tenantContext.TenantId is null) return (null, Unauthorized(new { message = "Kein Tenant im Token" }));
        if (adminOnly && !IsAnyAdmin) return (null, Forbid());

        var booking = await _db.Bookings.Include(b => b.Customer).Include(b => b.Service)
            .FirstOrDefaultAsync(b => b.Id == bookingId);
        if (booking == null) return (null, NotFound(new { message = "Buchung nicht gefunden" }));

        if (IsTenantWideAdmin) return (booking, null);
        if (IsLocationAdmin)
        {
            var loc = LocationScope;
            return loc != null && (booking.LocationId == loc || booking.Service.LocationId == loc)
                ? (booking, null) : (null, Forbid());
        }
        // Employee: only their own appointments.
        var employeeId = JwtService.GetEmployeeId(User);
        return employeeId != null && booking.EmployeeId == employeeId ? (booking, null) : (null, Forbid());
    }

    [HttpGet("for-booking/{bookingId:guid}")]
    public async Task<IActionResult> GetForBooking(Guid bookingId)
    {
        var (booking, deny) = await LoadAccessibleBookingAsync(bookingId, adminOnly: false);
        if (deny != null) return deny;
        var tenantId = _tenantContext.TenantId!.Value;

        var enabled = await TreatmentPackageRules.IsEnabledForTenantAsync(_db, tenantId);
        TreatmentPackageDto? assigned = null;
        List<TreatmentPackageDto> candidates = new();

        if (enabled)
        {
            if (booking!.TreatmentPackageId is { } pid)
            {
                var pkg = await _db.TreatmentPackages.Include(p => p.Customer).Include(p => p.Service)
                    .FirstOrDefaultAsync(p => p.Id == pid);
                if (pkg != null) assigned = (await ToDtosAsync(tenantId, new() { pkg }))[0];
            }

            // Only admins can (re)assign, so only they get the candidate list.
            if (IsAnyAdmin && booking.Status is BookingStatus.Pending or BookingStatus.Confirmed)
            {
                var possible = await VisiblePackages().Include(p => p.Customer).Include(p => p.Service)
                    .Where(p => p.CustomerId == booking.CustomerId && p.ServiceId == booking.ServiceId
                                && p.Status == TreatmentPackageStatus.Active && p.Id != booking.TreatmentPackageId)
                    .ToListAsync();
                var progress = await TreatmentPackageRules.GetProgressAsync(_db, possible, excludeBookingId: booking.Id);
                var bookable = possible.Where(p => progress[p.Id].BookableSessions > 0).ToList();
                candidates = await ToDtosAsync(tenantId, bookable);
            }
        }

        // A completed session is the debit record itself — its package link is frozen
        // (AssignToBooking rejects it), so don't offer link/unlink there at all.
        var canAssign = IsAnyAdmin && enabled && booking!.Status != BookingStatus.Completed;

        return Ok(new BookingPackageInfoDto(
            booking!.Id, booking.CustomerId, booking.Customer.FullName,
            booking.Customer.Notes, enabled, canAssign,
            assigned, candidates));
    }

    [HttpPut("for-booking/{bookingId:guid}")]
    public async Task<IActionResult> AssignToBooking(Guid bookingId, [FromBody] AssignTreatmentPackageDto dto)
    {
        var (tenantId, planDeny) = await RequireAdminWithPlanAsync();
        if (planDeny != null) return planDeny;

        // Serializable: two concurrent assignments must not both take the last open session.
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var (booking, deny) = await LoadAccessibleBookingAsync(bookingId, adminOnly: true);
        if (deny != null) return deny;

        if (booking!.Status == BookingStatus.Completed)
            return BadRequest(new { message = "Bei abgeschlossenen Terminen kann die Paketzuordnung nicht mehr geändert werden." });

        if (dto.TreatmentPackageId is { } packageId)
        {
            if (booking.Status is not (BookingStatus.Pending or BookingStatus.Confirmed))
                return BadRequest(new { message = "Nur offene Termine können einem Paket zugeordnet werden." });

            var package = await VisiblePackages().FirstOrDefaultAsync(p => p.Id == packageId);
            if (package == null) return NotFound(new { message = "Behandlungspaket nicht gefunden" });

            var error = await TreatmentPackageRules.ValidateAssignmentAsync(
                _db, package, booking.CustomerId, booking.ServiceId, excludeBookingId: booking.Id);
            if (error != null) return BadRequest(new { message = error });
            booking.TreatmentPackageId = package.Id;
        }
        else
        {
            booking.TreatmentPackageId = null;
        }

        booking.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        await _audit.LogAsync("booking.treatment_package_changed", "Booking", booking.Id.ToString(),
            dto.TreatmentPackageId?.ToString() ?? "Zuordnung entfernt", tenantId);

        return await GetForBooking(bookingId);
    }
}

// ── DTOs ─────────────────────────────────────────────────────────

public record CreateTreatmentPackageDto(Guid CustomerId, Guid ServiceId, int TotalSessions, string? Name = null, int? PreviouslyUsedSessions = 0);
public record UpdateTreatmentPackageDto(string? Name = null, int? TotalSessions = null, int? PreviouslyUsedSessions = null, string? Status = null);
public record AssignTreatmentPackageDto(Guid? TreatmentPackageId);

public record TreatmentPackageDto(
    Guid Id, Guid CustomerId, string CustomerName, Guid ServiceId, string ServiceName,
    string Name, int TotalSessions, int PreviouslyUsedSessions, int UsedSessions, int OpenSessions,
    int ReservedSessions, int BookableSessions,
    string Status,                 // "Active" | "UsedUp" | "Cancelled"
    string? NextAppointmentDate, string? NextAppointmentTime,
    string BookingLinkPath, DateTime CreatedAt);

public record FollowUpDto(
    Guid PackageId, string PackageName, Guid CustomerId, string CustomerName, string? CustomerPhone, string? CustomerEmail,
    Guid ServiceId, string ServiceName, int TotalSessions, int UsedSessions, int OpenSessions,
    string ReasonCode, string Reason, string? LastAppointmentDate, string BookingLinkPath);

public record BookingPackageInfoDto(
    Guid BookingId, Guid CustomerId, string CustomerName, string? CustomerInternalNotes,
    bool PackagesEnabled, bool CanAssign,
    TreatmentPackageDto? AssignedPackage, List<TreatmentPackageDto> Candidates);
