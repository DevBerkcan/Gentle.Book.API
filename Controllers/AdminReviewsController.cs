// Controllers/AdminReviewsController.cs
// Agency-exclusive: lets staff see customer reviews and decide which ones to publish on the
// public booking page. TenantAdmin/SuperAdmin/LocationAdmin — same admin-role set as
// AdminServicesController, since reviews hang off bookings the same way services do.
using GentleBook.Api.Configuration;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Controllers;

[ApiController]
[Route("api/admin/reviews")]
[Authorize]
public class AdminReviewsController : ControllerBase
{
    private readonly GentleBookDbContext _db;
    private readonly ITenantContext _tenantContext;
    private readonly ILogger<AdminReviewsController> _logger;

    public AdminReviewsController(GentleBookDbContext db, ITenantContext tenantContext, ILogger<AdminReviewsController> logger)
    {
        _db = db;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    private ObjectResult? RequireTenantAdmin()
    {
        var role = JwtService.GetRole(User);
        if (role != "TenantAdmin" && role != "SuperAdmin" && role != "LocationAdmin")
            return StatusCode(403, new { message = "Nur Administratoren dürfen Bewertungen verwalten." });
        return null;
    }

    private async Task<IActionResult?> RequireAgencyPlanAsync()
    {
        if (_tenantContext.TenantId is not { } tenantId) return Unauthorized(new { message = "Kein Tenant im Token" });

        var currentPlan = await _db.Subscriptions
            .Where(s => s.TenantId == tenantId)
            .Select(s => (SubscriptionPlan?)s.Plan)
            .FirstOrDefaultAsync() ?? SubscriptionPlan.Trial;

        var requiredPlanName = AgencyFeatureGate.ValidateForPlan(currentPlan);
        if (requiredPlanName != null)
        {
            return StatusCode(402, new
            {
                message = $"Bewertungen sind dem {requiredPlanName}-Plan vorbehalten.",
                feature = "reviews",
                upgrade = true,
                currentPlan = PlanLimits.Get(currentPlan).DisplayName,
                requiredPlan = requiredPlanName,
            });
        }
        return null;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var guard = RequireTenantAdmin();
        if (guard != null) return guard;
        if (await RequireAgencyPlanAsync() is { } deny) return deny;

        var reviews = await _db.Reviews
            .Include(r => r.Booking).ThenInclude(b => b.Service)
            .Include(r => r.Customer)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new
            {
                r.Id,
                r.Rating,
                r.Comment,
                r.IsPublished,
                r.CreatedAt,
                serviceName = r.Booking.Service.Name,
                customerName = r.Customer.FirstName + " " + r.Customer.LastName,
            })
            .ToListAsync();

        return Ok(reviews);
    }

    [HttpPut("{id:guid}/publish")]
    public async Task<IActionResult> SetPublished(Guid id, [FromBody] SetReviewPublishedRequest request)
    {
        var guard = RequireTenantAdmin();
        if (guard != null) return guard;
        if (await RequireAgencyPlanAsync() is { } deny) return deny;

        var review = await _db.Reviews.FirstOrDefaultAsync(r => r.Id == id);
        if (review == null) return NotFound();

        review.IsPublished = request.IsPublished;
        await _db.SaveChangesAsync();

        return Ok(new { review.Id, review.IsPublished });
    }
}

public record SetReviewPublishedRequest(bool IsPublished);
