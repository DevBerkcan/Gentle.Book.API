using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Controllers;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.DTOs;
using GentleBook.Api.Options;
using GentleBook.Api.Services;
using GentleBook.Api.Services.AI;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gentle.Book.API.Tests;

// Covers server-side plan-limit enforcement on employee creation (Starter=2, Professional=10) —
// had zero test coverage before this pass, despite being flagged as a gap alongside the
// Agency-feature audit.
public class EmployeesControllerLimitTests
{
    private static EmployeesController BuildController(GentleBook.Api.Data.GentleBookDbContext db, Guid tenantId)
    {
        var tenantContext = new TenantContext();
        tenantContext.Set(tenantId, "test-tenant", role: "TenantAdmin");

        var employeeService = new EmployeeService(db, NullLogger<EmployeeService>.Instance, tenantContext);
        var httpContextAccessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var audit = new AuditService(db, httpContextAccessor, NullLogger<AuditService>.Instance);
        var aiOptions = Options.Create(new AiProviderOptions());
        var openAiClient = new OpenAiClient(
            new HttpClient(new FakeHttpMessageHandler(_ => throw new InvalidOperationException("AI must not be called for a plain employee-create test."))) { BaseAddress = new Uri("https://api.openai.com/v1/") },
            new StaticOptionsMonitor<AiProviderOptions>(new AiProviderOptions()));

        var controller = new EmployeesController(
            employeeService, db, TestConfiguration.Build(), audit, new FakeWebHostEnvironment(),
            NullLogger<EmployeesController>.Instance, openAiClient, new AiUsageMeter(db), aiOptions);

        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(tenantContext);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                RequestServices = services.BuildServiceProvider(),
                User = ClaimsPrincipalFactory.TenantAdmin(Guid.NewGuid(), tenantId),
            },
        };
        return controller;
    }

    private static void SeedActiveEmployees(GentleBook.Api.Data.GentleBookDbContext db, Guid tenantId, int count)
    {
        for (var i = 0; i < count; i++)
            db.Employees.Add(new Employee { TenantId = tenantId, Name = $"Mitarbeiter {i}", Role = "Friseurin", IsActive = true });
        db.SaveChanges();
    }

    [Fact]
    public async Task Create_StarterAtEmployeeLimit_Returns402()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = AgencyTenantFactory.Seed(db, SubscriptionPlan.Starter);
        SeedActiveEmployees(db, tenant.Id, count: 2); // Starter MaxEmployees = 2
        var controller = BuildController(db, tenant.Id);

        var result = await controller.Create(new CreateEmployeeRequest("Neue Kraft", "Friseurin", null, null, null, null));

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(402, objectResult.StatusCode);
        Assert.Equal(2, await db.Employees.CountAsync(e => e.TenantId == tenant.Id));
    }

    [Fact]
    public async Task Create_StarterUnderEmployeeLimit_Succeeds()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = AgencyTenantFactory.Seed(db, SubscriptionPlan.Starter);
        SeedActiveEmployees(db, tenant.Id, count: 1); // 1 of 2 used
        var controller = BuildController(db, tenant.Id);

        var result = await controller.Create(new CreateEmployeeRequest("Neue Kraft", "Friseurin", null, null, null, null));

        Assert.IsType<CreatedAtActionResult>(result);
        Assert.Equal(2, await db.Employees.CountAsync(e => e.TenantId == tenant.Id));
    }

    [Fact]
    public async Task Create_ProfessionalAtSameHeadcountAsStarterLimit_StillSucceeds()
    {
        // Same headcount (2) that would block a Starter tenant must succeed on Professional
        // (limit 10) — proves the check reads the tenant's actual plan, not a fixed number.
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = AgencyTenantFactory.Seed(db, SubscriptionPlan.Professional);
        SeedActiveEmployees(db, tenant.Id, count: 2);
        var controller = BuildController(db, tenant.Id);

        var result = await controller.Create(new CreateEmployeeRequest("Neue Kraft", "Friseurin", null, null, null, null));

        Assert.IsType<CreatedAtActionResult>(result);
    }
}
