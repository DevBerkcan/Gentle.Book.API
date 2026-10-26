using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Controllers;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Options;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gentle.Book.API.Tests;

// Found during live pre-launch testing: SuperAdminController.ChangePlan unconditionally refused
// to set a tenant to Agency at all, even with a negotiated price attached — directing the
// SuperAdmin to the separate offer/request flow instead. The tenant requested this direct path
// also stay available (e.g. for an already-negotiated deal or internal testing), as long as a
// price is attached — same requirement the formal offer flow already enforces.
public class SuperAdminChangePlanTests
{
    private static (Tenant tenant, Subscription subscription, SuperAdminController controller) Build(
        GentleBook.Api.Data.GentleBookDbContext db, SubscriptionPlan startingPlan = SubscriptionPlan.Starter)
    {
        var tenant = new Tenant { Name = "Salon Planwechsel", Slug = "salon-planwechsel-" + Guid.NewGuid(), IsActive = true };
        var subscription = new Subscription { TenantId = tenant.Id, Tenant = tenant, Plan = startingPlan, Status = SubscriptionStatus.Active };
        tenant.Subscription = subscription;
        db.AddRange(tenant, subscription);
        db.SaveChanges();

        var httpContext = new DefaultHttpContext { User = ClaimsPrincipalFactory.SuperAdmin(Guid.NewGuid()) };
        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        var audit = new AuditService(db, accessor, NullLogger<AuditService>.Instance);
        var emailService = TestServiceFactory.CreateEmailService(db);
        var mollieOptions = Options.Create(new MollieOptions());
        var mollieClient = new MollieClient(new HttpClient(), new StaticOptionsMonitor<MollieOptions>(new MollieOptions()));
        var mollieService = new MollieService(
            db, mollieClient, mollieOptions, new FakeBackgroundJobClient(), audit, emailService, NullLogger<MollieService>.Instance);
        var controller = new SuperAdminController(
            db,
            TestConfiguration.Build(),
            emailService,
            NullLogger<SuperAdminController>.Instance,
            new JwtService(TestConfiguration.Build()),
            new FakeWebHostEnvironment(),
            audit,
            mollieService,
            new FakeBackgroundJobClient(),
            TestServiceFactory.CreateDataSubjectRequestService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
        return (tenant, subscription, controller);
    }

    [Fact]
    public async Task ChangePlan_ToAgencyWithoutPrice_Rejected()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _, controller) = Build(db);

        var result = await controller.ChangePlan(tenant.Id, new ChangePlanDto("Agency"));

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("verhandelter Preis", bad.Value!.ToString());
    }

    [Fact]
    public async Task ChangePlan_ToAgencyWithMonthlyPrice_Succeeds()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, subscription, controller) = Build(db);

        var result = await controller.ChangePlan(tenant.Id, new ChangePlanDto("Agency", NegotiatedMonthlyPrice: 149m));

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(SubscriptionPlan.Agency, subscription.Plan);
        Assert.Equal(149m, subscription.NegotiatedMonthlyPrice);
        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
    }

    [Fact]
    public async Task ChangePlan_ToAgencyWithExistingPriceAlreadyOnFile_SucceedsWithoutResendingPrice()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, subscription, controller) = Build(db);
        subscription.NegotiatedMonthlyPrice = 149m;
        db.SaveChanges();

        var result = await controller.ChangePlan(tenant.Id, new ChangePlanDto("Agency"));

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(SubscriptionPlan.Agency, subscription.Plan);
        Assert.Equal(149m, subscription.NegotiatedMonthlyPrice);
    }

    [Fact]
    public async Task ChangePlan_ToProfessional_StillWorksUnaffected()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, subscription, controller) = Build(db);

        var result = await controller.ChangePlan(tenant.Id, new ChangePlanDto("Professional"));

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(SubscriptionPlan.Professional, subscription.Plan);
    }
}
