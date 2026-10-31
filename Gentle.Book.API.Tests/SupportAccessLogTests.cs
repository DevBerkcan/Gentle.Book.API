using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Controllers;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gentle.Book.API.Tests;

// Tenant-facing half of the support-access audit trail (see ImpersonateTenantReasonTests for the
// SuperAdmin side that writes these entries): a TenantAdmin should be able to see when/why/by
// whom GentleBook support logged into their account, scoped strictly to their own tenant.
public class SupportAccessLogTests
{
    private static AdminController BuildController(GentleBook.Api.Data.GentleBookDbContext db, System.Security.Claims.ClaimsPrincipal user)
    {
        var httpContext = new DefaultHttpContext { User = user };
        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        var audit = new AuditService(db, accessor, NullLogger<AuditService>.Instance);
        var emailService = TestServiceFactory.CreateEmailService(db);
        var adminService = new AdminService(db, NullLogger<AdminService>.Instance, emailService,
            new LoyaltyService(db, NullLogger<LoyaltyService>.Instance), TestServiceFactory.CreateVoucherService(db),
            new FakeBackgroundJobClient(), new TenantContext());
        var manualBookingService = new ManualBookingService(db, NullLogger<ManualBookingService>.Instance, emailService,
            new TenantContext(), TestServiceFactory.CreateVoucherService(db));

        return new AdminController(adminService, NullLogger<AdminController>.Instance, manualBookingService, emailService, db, audit)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    private static async Task SeedImpersonationEntry(GentleBook.Api.Data.GentleBookDbContext db, Guid tenantId, string details)
    {
        var httpContext = new DefaultHttpContext { User = ClaimsPrincipalFactory.SuperAdmin(Guid.NewGuid()) };
        var audit = new AuditService(db, new HttpContextAccessor { HttpContext = httpContext }, NullLogger<AuditService>.Instance);
        await audit.LogAsync("tenant.impersonated", "Tenant", tenantId.ToString(), details, tenantId);
    }

    [Fact]
    public async Task GetSupportAccessLog_TenantAdmin_SeesOwnTenantsImpersonationEntries()
    {
        using var db = TestDbContextFactory.Create();
        var tenantId = Guid.NewGuid();
        await SeedImpersonationEntry(db, tenantId, "Impersonation als owner@salon.de für System \"Salon\" — Grund: Ticket #42");

        var controller = BuildController(db, ClaimsPrincipalFactory.TenantAdmin(Guid.NewGuid(), tenantId));
        var result = await controller.GetSupportAccessLog();

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("Ticket #42", json);
    }

    [Fact]
    public async Task GetSupportAccessLog_DoesNotLeakOtherTenantsEntries()
    {
        using var db = TestDbContextFactory.Create();
        var ownTenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        await SeedImpersonationEntry(db, otherTenantId, "Impersonation als jemand@anderes.de für System \"Anderer Salon\" — Grund: Geheim");

        var controller = BuildController(db, ClaimsPrincipalFactory.TenantAdmin(Guid.NewGuid(), ownTenantId));
        var result = await controller.GetSupportAccessLog();

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.DoesNotContain("Geheim", json);
    }

    [Fact]
    public async Task GetSupportAccessLog_Employee_Forbidden()
    {
        using var db = TestDbContextFactory.Create();
        var tenantId = Guid.NewGuid();
        await SeedImpersonationEntry(db, tenantId, "Impersonation als owner@salon.de für System \"Salon\" — Grund: Ticket #42");

        var controller = BuildController(db, ClaimsPrincipalFactory.Employee(Guid.NewGuid(), tenantId));
        var result = await controller.GetSupportAccessLog();

        Assert.IsType<ForbidResult>(result);
    }
}
