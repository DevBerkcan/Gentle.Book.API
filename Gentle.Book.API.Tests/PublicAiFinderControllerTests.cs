using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.DTOs;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Gentle.Book.API.Tests;

/// <summary>
/// Covers the Copilot-audit findings on PublicAiFinderController: /api/public/* is exempt from
/// TenantMiddleware's subscription gate (no JWT to resolve a tenant from), so the controller must
/// enforce it itself — and Evaluate/CreateBookingDraft/ConfirmBookingDraft never re-checked
/// IsFinderEnabled or the tenant's plan at all, only GetBootstrap did.
/// </summary>
public class PublicAiFinderControllerTests
{
    private static Tenant SeedTenant(
        GentleBook.Api.Data.GentleBookDbContext db,
        SubscriptionPlan plan,
        SubscriptionStatus status,
        bool? isFinderEnabled)
    {
        var tenant = new Tenant { Name = "Barber Wagner", Slug = "barber-wagner", IsActive = true };
        var subscription = new Subscription { TenantId = tenant.Id, Tenant = tenant, Plan = plan, Status = status };
        tenant.Subscription = subscription;

        db.Tenants.Add(tenant);
        db.Subscriptions.Add(subscription);

        if (isFinderEnabled.HasValue)
        {
            db.TenantIndustrySettings.Add(new TenantIndustrySetting
            {
                TenantId = tenant.Id,
                PrimaryIndustryProfileId = Guid.NewGuid(),
                IsFinderEnabled = isFinderEnabled.Value,
            });
        }

        db.SaveChanges();
        return tenant;
    }

    private static EvaluateFinderRequestDto EmptyEvaluateRequest() =>
        new(new List<FinderAnswerValueDto>(), FreeText: null);

    [Fact]
    public async Task Evaluate_SubscriptionInactive_Returns402()
    {
        using var db = TestDbContextFactory.Create();
        SeedTenant(db, SubscriptionPlan.Agency, SubscriptionStatus.Cancelled, isFinderEnabled: true);
        var controller = PublicAiFinderControllerFactory.Create(db);

        var result = await controller.Evaluate("barber-wagner", EmptyEvaluateRequest(), CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(402, objectResult.StatusCode);
    }

    [Fact]
    public async Task Evaluate_FinderDisabled_IsRejected()
    {
        using var db = TestDbContextFactory.Create();
        SeedTenant(db, SubscriptionPlan.Agency, SubscriptionStatus.Active, isFinderEnabled: false);
        var controller = PublicAiFinderControllerFactory.Create(db);

        var result = await controller.Evaluate("barber-wagner", EmptyEvaluateRequest(), CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task Evaluate_NoIndustrySettingsAtAll_IsRejected()
    {
        using var db = TestDbContextFactory.Create();
        SeedTenant(db, SubscriptionPlan.Agency, SubscriptionStatus.Active, isFinderEnabled: null);
        var controller = PublicAiFinderControllerFactory.Create(db);

        var result = await controller.Evaluate("barber-wagner", EmptyEvaluateRequest(), CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task Evaluate_PlanWithoutFinderAccess_Returns402()
    {
        // Finder was enabled while on a higher plan, tenant then downgraded to Starter —
        // defense in depth even though the admin toggle itself already blocks re-enabling.
        using var db = TestDbContextFactory.Create();
        SeedTenant(db, SubscriptionPlan.Starter, SubscriptionStatus.Active, isFinderEnabled: true);
        var controller = PublicAiFinderControllerFactory.Create(db);

        var result = await controller.Evaluate("barber-wagner", EmptyEvaluateRequest(), CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(402, objectResult.StatusCode);
    }

    [Fact]
    public async Task GetBootstrap_TenantNotFound_Returns404()
    {
        using var db = TestDbContextFactory.Create();
        var controller = PublicAiFinderControllerFactory.Create(db);

        var result = await controller.GetBootstrap("does-not-exist", CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task GetBootstrap_PlanWithoutFinderAccess_ReturnsSoftDisabled()
    {
        // Bootstrap must not expose a form the tenant can't actually submit — same plan gate as
        // Evaluate, but a graceful { enabled: false } instead of a hard 402.
        using var db = TestDbContextFactory.Create();
        SeedTenant(db, SubscriptionPlan.Starter, SubscriptionStatus.Active, isFinderEnabled: true);
        var controller = PublicAiFinderControllerFactory.Create(db);

        var result = await controller.GetBootstrap("barber-wagner", CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
    }
}
