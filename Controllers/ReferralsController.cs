// Controllers/ReferralsController.cs
// Studio side of "Freunde machen Freude" + the customer's Studio-Pass link in the Kundenkartei.
// Tenant isolation via the global query filters (Customer, Referral); admins only
// (TenantAdmin / SuperAdmin impersonation / LocationAdmin). Settings changes: TenantAdmin only.
using GentleBook.Api.Configuration;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Controllers;

[ApiController]
[Route("api/referrals")]
[Authorize]
public class ReferralsController : ControllerBase
{
    private readonly GentleBookDbContext _db;
    private readonly ITenantContext _tenantContext;
    private readonly AuditService _audit;

    public ReferralsController(GentleBookDbContext db, ITenantContext tenantContext, AuditService audit)
    {
        _db = db;
        _tenantContext = tenantContext;
        _audit = audit;
    }

    private string? Role => JwtService.GetRole(User);
    private bool IsOwner => Role is "TenantAdmin" or "SuperAdmin";
    private bool IsAnyAdmin => IsOwner || Role == "LocationAdmin";

    private (Guid tenantId, IActionResult? deny) RequireAdmin(bool ownerOnly = false)
    {
        if (_tenantContext.TenantId is not { } tenantId) return (Guid.Empty, Unauthorized(new { message = "Kein Tenant im Token" }));
        if (ownerOnly ? !IsOwner : !IsAnyAdmin) return (tenantId, Forbid());
        return (tenantId, null);
    }

    private async Task<IActionResult?> RequirePlanAsync(Guid tenantId)
    {
        var plan = await TreatmentPackageRules.GetPlanAsync(_db, tenantId);
        if (ReferralGate.IsAllowed(plan)) return null;
        return StatusCode(402, new
        {
            message = "„Freunde machen Freude“ ist in den Tarifen Pro und Agency enthalten.",
            feature = ReferralGate.FeatureKey,
            upgrade = true,
            currentPlan = PlanLimits.Get(plan).DisplayName,
            requiredPlan = PlanLimits.Get(SubscriptionPlan.Professional).DisplayName,
        });
    }

    // ── Settings ─────────────────────────────────────────────────

    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings()
    {
        var (tenantId, deny) = RequireAdmin();
        if (deny != null) return deny;
        var plan = await TreatmentPackageRules.GetPlanAsync(_db, tenantId);
        var s = await _db.TenantSettings.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId);
        return Ok(new
        {
            available = ReferralGate.IsAllowed(plan),
            enabled = s?.ReferralEnabled == true,
            rewardText = s?.ReferralRewardText,
            defaultRewardText = StudioPassRules.DefaultRewardText,
        });
    }

    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings([FromBody] UpdateReferralSettingsDto dto)
    {
        var (tenantId, deny) = RequireAdmin(ownerOnly: true);
        if (deny != null) return deny;
        if (await RequirePlanAsync(tenantId) is { } planDeny) return planDeny;

        var text = dto.RewardText?.Trim();
        if (text?.Length > ReferralGate.MaxRewardTextLength)
            return BadRequest(new { message = $"Die Prämie darf höchstens {ReferralGate.MaxRewardTextLength} Zeichen lang sein." });
        if (dto.Enabled && string.IsNullOrEmpty(text))
            return BadRequest(new { message = "Bitte beschreiben Sie die Prämie, z. B. „Nail-Art-Upgrade beim nächsten Besuch“." });

        var s = await _db.TenantSettings.FirstOrDefaultAsync(x => x.TenantId == tenantId);
        if (s == null) return NotFound();
        s.ReferralEnabled = dto.Enabled;
        s.ReferralRewardText = string.IsNullOrEmpty(text) ? null : text;
        s.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.LogAsync("referral.settings_updated", "TenantSettings", s.Id.ToString(),
            $"{(dto.Enabled ? "aktiv" : "inaktiv")}: {text}", tenantId);

        return Ok(new { available = true, enabled = s.ReferralEnabled, rewardText = s.ReferralRewardText, defaultRewardText = StudioPassRules.DefaultRewardText });
    }

    // ── Dashboard ────────────────────────────────────────────────

    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        var (tenantId, deny) = RequireAdmin();
        if (deny != null) return deny;
        if (await RequirePlanAsync(tenantId) is { } planDeny) return planDeny;

        var enabled = await _db.TenantSettings.Where(s => s.TenantId == tenantId).Select(s => s.ReferralEnabled).FirstOrDefaultAsync();
        var referrals = await _db.Referrals
            .Include(r => r.ReferrerCustomer).Include(r => r.ReferredCustomer)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();

        var qualifiedReferred = referrals.Where(r => r.Status == ReferralStatus.Qualified).Select(r => r.ReferredCustomerId).ToList();
        var secondBookings = qualifiedReferred.Count == 0 ? 0 : await _db.Bookings
            .Where(b => qualifiedReferred.Contains(b.CustomerId) && b.Status != BookingStatus.Cancelled)
            .GroupBy(b => b.CustomerId)
            .CountAsync(g => g.Count() >= 2);

        return Ok(new
        {
            enabled,
            bookedInvitations = referrals.Count,
            firstVisits = referrals.Count(r => r.Status == ReferralStatus.Qualified),
            rewardsEarned = referrals.Count(r => r.Status == ReferralStatus.Qualified) * 2,
            rewardsRedeemed = referrals.Count(r => r.ReferrerRewardRedeemedAt != null) + referrals.Count(r => r.ReferredRewardRedeemedAt != null),
            secondBookings,
            recent = referrals.Take(10).Select(r => new
            {
                id = r.Id,
                referrerName = r.ReferrerCustomer.FullName,
                referredName = r.ReferredCustomer.FullName,
                status = r.Status.ToString(),
                createdAt = r.CreatedAt,
                qualifiedAt = r.QualifiedAt,
            }),
        });
    }

    // ── Kundenkartei: pass link + referral view ──────────────────

    [HttpGet("customer/{customerId:guid}")]
    public async Task<IActionResult> GetForCustomer(Guid customerId)
    {
        var (tenantId, deny) = RequireAdmin();
        if (deny != null) return deny;

        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == customerId);
        if (customer == null) return NotFound(new { message = "Kunde nicht gefunden" });

        var passToken = await StudioPassRules.EnsurePassTokenPersistedAsync(_db, customer.Id);

        var plan = await TreatmentPackageRules.GetPlanAsync(_db, tenantId);
        var referralAvailable = ReferralGate.IsAllowed(plan);
        var referrals = referralAvailable
            ? await _db.Referrals.Include(r => r.ReferrerCustomer).Include(r => r.ReferredCustomer)
                .Where(r => r.ReferrerCustomerId == customerId || r.ReferredCustomerId == customerId)
                .OrderByDescending(r => r.CreatedAt).ToListAsync()
            : new List<Referral>();

        var referredBy = referrals.FirstOrDefault(r => r.ReferredCustomerId == customerId);
        var rewards = referrals.Where(r => r.Status == ReferralStatus.Qualified).Select(r =>
        {
            var asReferrer = r.ReferrerCustomerId == customerId;
            return new
            {
                referralId = r.Id,
                side = asReferrer ? "referrer" : "referred",
                text = r.RewardText ?? StudioPassRules.DefaultRewardText,
                reason = asReferrer ? $"Empfehlung: {r.ReferredCustomer.FullName}" : $"Willkommensbonus (empfohlen von {r.ReferrerCustomer.FullName})",
                redeemedAt = asReferrer ? r.ReferrerRewardRedeemedAt : r.ReferredRewardRedeemedAt,
            };
        }).ToList();

        return Ok(new
        {
            passPath = $"/pass/{passToken}",
            referralAvailable,
            referredBy = referredBy == null ? null : new { name = referredBy.ReferrerCustomer.FullName, status = referredBy.Status.ToString() },
            invited = referrals.Where(r => r.ReferrerCustomerId == customerId).Select(r => new
            {
                name = r.ReferredCustomer.FullName,
                status = r.Status.ToString(),
                createdAt = r.CreatedAt,
                qualifiedAt = r.QualifiedAt,
            }),
            rewards,
        });
    }

    /// <summary>Invalidates the customer's old Studio-Pass link (e.g. forwarded by mistake) and issues a new one.</summary>
    [HttpPost("customer/{customerId:guid}/pass/renew")]
    public async Task<IActionResult> RenewPass(Guid customerId)
    {
        var (tenantId, deny) = RequireAdmin();
        if (deny != null) return deny;
        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == customerId);
        if (customer == null) return NotFound(new { message = "Kunde nicht gefunden" });

        customer.PassToken = StudioPassRules.NewPassToken();
        customer.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.LogAsync("customer.pass_renewed", "Customer", customer.Id.ToString(), null, tenantId);
        return Ok(new { passPath = $"/pass/{customer.PassToken}" });
    }

    /// <summary>Marks one side's referral bonus as redeemed (or reverts that, for a mis-click).</summary>
    [HttpPost("{referralId:guid}/redeem")]
    public async Task<IActionResult> Redeem(Guid referralId, [FromBody] RedeemReferralRewardDto dto)
    {
        var (tenantId, deny) = RequireAdmin();
        if (deny != null) return deny;

        var r = await _db.Referrals.FirstOrDefaultAsync(x => x.Id == referralId);
        if (r == null) return NotFound(new { message = "Empfehlung nicht gefunden" });
        if (r.Status != ReferralStatus.Qualified)
            return BadRequest(new { message = "Der Bonus wird erst nach dem ersten wahrgenommenen Termin der Freundin freigeschaltet." });

        DateTime? value = dto.Undo ? null : DateTime.UtcNow;
        switch (dto.Side)
        {
            case "referrer": r.ReferrerRewardRedeemedAt = dto.Undo ? null : r.ReferrerRewardRedeemedAt ?? value; break;
            case "referred": r.ReferredRewardRedeemedAt = dto.Undo ? null : r.ReferredRewardRedeemedAt ?? value; break;
            default: return BadRequest(new { message = "side muss referrer oder referred sein." });
        }
        await _db.SaveChangesAsync();
        await _audit.LogAsync(dto.Undo ? "referral.reward_unredeemed" : "referral.reward_redeemed", "Referral", r.Id.ToString(), dto.Side, tenantId);
        return Ok(new { referralId = r.Id, referrerRedeemedAt = r.ReferrerRewardRedeemedAt, referredRedeemedAt = r.ReferredRewardRedeemedAt });
    }
}

public record UpdateReferralSettingsDto(bool Enabled, string? RewardText);
public record RedeemReferralRewardDto(string Side, bool Undo = false);
