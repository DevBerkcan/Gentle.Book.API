using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Controllers;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Options;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gentle.Book.API.Tests;

// SuperAdminController.PurchaseAiBudgetPackage — the "sell a KI-Budget-Paket (20/50/100€),
// top up the tenant's AI budget, invoice them for it" upsell flow a SuperAdmin uses after being
// alerted a tenant hit its hard cap (AiBudgetService.SendAiBudgetHardCapOpsAlertAsync).
public class AiBudgetPackagePurchaseTests
{
    private static (Tenant tenant, Subscription subscription, SuperAdminController controller, FakeBackgroundJobClient jobs) Build(
        GentleBook.Api.Data.GentleBookDbContext db)
    {
        var tenant = new Tenant { Name = "Salon Budget-Paket", Slug = "salon-budget-paket-" + Guid.NewGuid(), IsActive = true };
        var subscription = new Subscription { TenantId = tenant.Id, Tenant = tenant, Plan = SubscriptionPlan.Agency, Status = SubscriptionStatus.Active };
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
        var jobs = new FakeBackgroundJobClient();
        var controller = new SuperAdminController(
            db, TestConfiguration.Build(), emailService, NullLogger<SuperAdminController>.Instance,
            new JwtService(TestConfiguration.Build()), new FakeWebHostEnvironment(), audit, mollieService, jobs,
            TestServiceFactory.CreateDataSubjectRequestService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
        return (tenant, subscription, controller, jobs);
    }

    [Fact]
    public async Task PurchasePackage_AddsToExistingBudgetAndEnqueuesInvoice()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _, controller, jobs) = Build(db);
        db.TenantAiBudgets.Add(new TenantAiBudget { TenantId = tenant.Id, MonthlyBudgetUsd = 20m, CurrentPeriodCostUsd = 20m, IsHardCapped = true });
        await db.SaveChangesAsync();

        var result = await controller.PurchaseAiBudgetPackage(tenant.Id, new PurchaseAiBudgetPackageDto(50m, 50m, "Paket B"));

        Assert.IsType<OkObjectResult>(result);
        var budget = await db.TenantAiBudgets.SingleAsync(b => b.TenantId == tenant.Id);
        Assert.Equal(70m, budget.MonthlyBudgetUsd); // 20 existing + 50 top-up
        Assert.False(budget.IsHardCapped); // new budget covers current spend again
        Assert.Single(jobs.CreatedJobs);
        Assert.Equal(nameof(InvoiceService.GenerateAndSendInvoiceAsync), jobs.CreatedJobs[0].Method.Name);
        Assert.Contains(db.AuditLogs, a => a.Action == "ai_budget.package_purchased" && a.TenantId == tenant.Id);
    }

    [Fact]
    public async Task PurchasePackage_NoExistingBudgetRow_StartsFromPlatformDefault()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _, controller, _) = Build(db);

        var result = await controller.PurchaseAiBudgetPackage(tenant.Id, new PurchaseAiBudgetPackageDto(20m, 20m, "Paket A"));

        Assert.IsType<OkObjectResult>(result);
        var budget = await db.TenantAiBudgets.SingleAsync(b => b.TenantId == tenant.Id);
        Assert.Equal(40m, budget.MonthlyBudgetUsd); // 20 platform default + 20 top-up
    }

    [Fact]
    public async Task PurchasePackage_NoSubscription_IsNotFound()
    {
        using var db = TestDbContextFactory.Create();
        var tenant = new Tenant { Name = "Salon Ohne Abo", Slug = "salon-ohne-abo-" + Guid.NewGuid(), IsActive = true };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        var httpContext = new DefaultHttpContext { User = ClaimsPrincipalFactory.SuperAdmin(Guid.NewGuid()) };
        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        var audit = new AuditService(db, accessor, NullLogger<AuditService>.Instance);
        var emailService = TestServiceFactory.CreateEmailService(db);
        var mollieOptions = Options.Create(new MollieOptions());
        var mollieClient = new MollieClient(new HttpClient(), new StaticOptionsMonitor<MollieOptions>(new MollieOptions()));
        var mollieService = new MollieService(db, mollieClient, mollieOptions, new FakeBackgroundJobClient(), audit, emailService, NullLogger<MollieService>.Instance);
        var controller = new SuperAdminController(
            db, TestConfiguration.Build(), emailService, NullLogger<SuperAdminController>.Instance,
            new JwtService(TestConfiguration.Build()), new FakeWebHostEnvironment(), audit, mollieService, new FakeBackgroundJobClient(),
            TestServiceFactory.CreateDataSubjectRequestService(db))
        { ControllerContext = new ControllerContext { HttpContext = httpContext } };

        var result = await controller.PurchaseAiBudgetPackage(tenant.Id, new PurchaseAiBudgetPackageDto(20m, 20m, "Paket A"));

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Theory]
    [InlineData(0, 20, "Paket A")]
    [InlineData(20, 0, "Paket A")]
    [InlineData(20, 20, "")]
    public async Task PurchasePackage_InvalidInput_IsRejected(decimal additionalUsd, decimal priceEur, string label)
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _, controller, jobs) = Build(db);

        var result = await controller.PurchaseAiBudgetPackage(tenant.Id, new PurchaseAiBudgetPackageDto(additionalUsd, priceEur, label));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(jobs.CreatedJobs);
    }
}
