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

// Covers the "customer wants more AI capacity" loop: a tenant admin submits a request
// (AiFinderAdminController.RequestBudgetIncrease), a SuperAdmin approves or declines it
// (SuperAdminController.ApproveAiBudgetRequest/DeclineAiBudgetRequest). Stored as an AiAction —
// no new entity/migration needed, it already models "AI-related action awaiting confirmation".
public class AiBudgetIncreaseRequestTests
{
    private static SuperAdminController BuildSuperAdminController(GentleBook.Api.Data.GentleBookDbContext db)
    {
        var httpContext = new DefaultHttpContext { User = ClaimsPrincipalFactory.SuperAdmin(Guid.NewGuid()) };
        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        var audit = new AuditService(db, accessor, NullLogger<AuditService>.Instance);
        var emailService = TestServiceFactory.CreateEmailService(db);
        var mollieOptions = Options.Create(new MollieOptions());
        var mollieClient = new MollieClient(new HttpClient(), new StaticOptionsMonitor<MollieOptions>(new MollieOptions()));
        var mollieService = new MollieService(
            db, mollieClient, mollieOptions, new FakeBackgroundJobClient(), audit, emailService, NullLogger<MollieService>.Instance);
        return new SuperAdminController(
            db, TestConfiguration.Build(), emailService, NullLogger<SuperAdminController>.Instance,
            new JwtService(TestConfiguration.Build()), new FakeWebHostEnvironment(), audit, mollieService, new FakeBackgroundJobClient(),
            TestServiceFactory.CreateDataSubjectRequestService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    private static Tenant SeedTenant(GentleBook.Api.Data.GentleBookDbContext db)
    {
        var tenant = new Tenant { Name = "Salon Budget-Anfrage", Slug = "salon-budget-req-" + Guid.NewGuid(), IsActive = true };
        db.Tenants.Add(tenant);
        db.SaveChanges();
        return tenant;
    }

    [Fact]
    public async Task RequestBudgetIncrease_ValidAmount_CreatesPendingAiAction()
    {
        using var db = TestDbContextFactory.Create();
        var tenant = SeedTenant(db);
        var controller = AiFinderAdminControllerFactory.Create(db, tenant.Id, "TenantAdmin");

        var result = await controller.RequestBudgetIncrease(new BudgetIncreaseRequestDto(50m, "Wachsen stark"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var action = await db.AiActions.SingleAsync(a => a.TenantId == tenant.Id);
        Assert.Equal("ai_budget_increase_request", action.ActionType);
        Assert.Equal("Pending", action.Status);
        Assert.Contains("50", action.InputJson);
    }

    [Fact]
    public async Task RequestBudgetIncrease_AlreadyHasPendingRequest_IsRejected()
    {
        using var db = TestDbContextFactory.Create();
        var tenant = SeedTenant(db);
        var controller = AiFinderAdminControllerFactory.Create(db, tenant.Id, "TenantAdmin");
        await controller.RequestBudgetIncrease(new BudgetIncreaseRequestDto(50m, null), CancellationToken.None);

        var result = await controller.RequestBudgetIncrease(new BudgetIncreaseRequestDto(80m, null), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Single(db.AiActions.Where(a => a.TenantId == tenant.Id));
    }

    [Fact]
    public async Task GetOverview_PendingRequestExists_IsReflectedInResponse()
    {
        using var db = TestDbContextFactory.Create();
        var tenant = SeedTenant(db);
        var controller = AiFinderAdminControllerFactory.Create(db, tenant.Id, "TenantAdmin");
        await controller.RequestBudgetIncrease(new BudgetIncreaseRequestDto(50m, null), CancellationToken.None);

        var result = await controller.GetOverview(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("pendingBudgetRequest", json);
        Assert.Contains("50", json);
    }

    [Fact]
    public async Task ApproveAiBudgetRequest_SetsBudgetAndMarksApproved()
    {
        using var db = TestDbContextFactory.Create();
        var tenant = SeedTenant(db);
        var tenantController = AiFinderAdminControllerFactory.Create(db, tenant.Id, "TenantAdmin");
        await tenantController.RequestBudgetIncrease(new BudgetIncreaseRequestDto(50m, "brauche mehr"), CancellationToken.None);
        var action = await db.AiActions.SingleAsync(a => a.TenantId == tenant.Id);

        var superAdminController = BuildSuperAdminController(db);
        var result = await superAdminController.ApproveAiBudgetRequest(action.Id, null);

        Assert.IsType<OkObjectResult>(result);
        var budget = await db.TenantAiBudgets.SingleAsync(b => b.TenantId == tenant.Id);
        Assert.Equal(50m, budget.MonthlyBudgetUsd);
        var reloadedAction = await db.AiActions.SingleAsync(a => a.Id == action.Id);
        Assert.Equal("Approved", reloadedAction.Status);
        Assert.NotNull(reloadedAction.ConfirmedOn);
    }

    [Fact]
    public async Task ApproveAiBudgetRequest_TenantWasHardCapped_ClearsCapWhenNewBudgetCoversSpend()
    {
        using var db = TestDbContextFactory.Create();
        var tenant = SeedTenant(db);
        db.TenantAiBudgets.Add(new TenantAiBudget { TenantId = tenant.Id, MonthlyBudgetUsd = 10m, CurrentPeriodCostUsd = 10m, IsHardCapped = true });
        await db.SaveChangesAsync();
        var tenantController = AiFinderAdminControllerFactory.Create(db, tenant.Id, "TenantAdmin");
        await tenantController.RequestBudgetIncrease(new BudgetIncreaseRequestDto(50m, null), CancellationToken.None);
        var action = await db.AiActions.SingleAsync(a => a.TenantId == tenant.Id);

        var superAdminController = BuildSuperAdminController(db);
        await superAdminController.ApproveAiBudgetRequest(action.Id, null);

        var budget = await db.TenantAiBudgets.SingleAsync(b => b.TenantId == tenant.Id);
        Assert.False(budget.IsHardCapped);
    }

    [Fact]
    public async Task ApproveAiBudgetRequest_ExplicitAmountOverridesRequestedAmount()
    {
        using var db = TestDbContextFactory.Create();
        var tenant = SeedTenant(db);
        var tenantController = AiFinderAdminControllerFactory.Create(db, tenant.Id, "TenantAdmin");
        await tenantController.RequestBudgetIncrease(new BudgetIncreaseRequestDto(50m, null), CancellationToken.None);
        var action = await db.AiActions.SingleAsync(a => a.TenantId == tenant.Id);

        var superAdminController = BuildSuperAdminController(db);
        await superAdminController.ApproveAiBudgetRequest(action.Id, new ApproveAiBudgetRequestDto(35m));

        var budget = await db.TenantAiBudgets.SingleAsync(b => b.TenantId == tenant.Id);
        Assert.Equal(35m, budget.MonthlyBudgetUsd);
    }

    [Fact]
    public async Task DeclineAiBudgetRequest_LeavesBudgetUnchanged()
    {
        using var db = TestDbContextFactory.Create();
        var tenant = SeedTenant(db);
        var tenantController = AiFinderAdminControllerFactory.Create(db, tenant.Id, "TenantAdmin");
        await tenantController.RequestBudgetIncrease(new BudgetIncreaseRequestDto(50m, null), CancellationToken.None);
        var action = await db.AiActions.SingleAsync(a => a.TenantId == tenant.Id);

        var superAdminController = BuildSuperAdminController(db);
        var result = await superAdminController.DeclineAiBudgetRequest(action.Id);

        Assert.IsType<OkObjectResult>(result);
        Assert.False(await db.TenantAiBudgets.AnyAsync(b => b.TenantId == tenant.Id));
        var reloadedAction = await db.AiActions.SingleAsync(a => a.Id == action.Id);
        Assert.Equal("Declined", reloadedAction.Status);
    }

    [Fact]
    public async Task ApproveAiBudgetRequest_AlreadyProcessed_IsRejected()
    {
        using var db = TestDbContextFactory.Create();
        var tenant = SeedTenant(db);
        var tenantController = AiFinderAdminControllerFactory.Create(db, tenant.Id, "TenantAdmin");
        await tenantController.RequestBudgetIncrease(new BudgetIncreaseRequestDto(50m, null), CancellationToken.None);
        var action = await db.AiActions.SingleAsync(a => a.TenantId == tenant.Id);
        var superAdminController = BuildSuperAdminController(db);
        await superAdminController.DeclineAiBudgetRequest(action.Id);

        var result = await superAdminController.ApproveAiBudgetRequest(action.Id, null);

        Assert.IsType<BadRequestObjectResult>(result);
    }
}
