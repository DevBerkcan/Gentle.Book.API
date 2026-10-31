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

// Support access must be "nur auf Anlass, protokolliert" per the AVV (see the legal-prep audit) —
// a SuperAdmin impersonating a tenant now has to state why, and that reason is written into the
// same audit-log entry the tenant can later see via AdminController.GetSupportAccessLog.
public class ImpersonateTenantReasonTests
{
    private static (Tenant tenant, PlatformUser admin, SuperAdminController controller) Build(
        GentleBook.Api.Data.GentleBookDbContext db)
    {
        var tenant = new Tenant { Name = "Salon Support-Test", Slug = "salon-support-" + Guid.NewGuid(), IsActive = true };
        var admin = new PlatformUser
        {
            TenantId = tenant.Id,
            Tenant = tenant,
            Email = "owner@salon-support.de",
            Role = PlatformRole.TenantAdmin,
            PasswordHash = "x",
            CreatedAt = DateTime.UtcNow,
        };
        db.AddRange(tenant, admin);
        db.SaveChanges();

        var httpContext = new DefaultHttpContext { User = ClaimsPrincipalFactory.SuperAdmin(Guid.NewGuid()) };
        httpContext.User.Identities.First().AddClaim(new System.Security.Claims.Claim(
            System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Email, "berkcan@gentlegroup.de"));
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
        return (tenant, admin, controller);
    }

    [Fact]
    public async Task ImpersonateTenant_WithoutReason_Rejected()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _, controller) = Build(db);

        var result = await controller.ImpersonateTenant(tenant.Id, new ImpersonateTenantDto(""));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.False(await db.AuditLogs.AnyAsync(a => a.TenantId == tenant.Id));
    }

    [Fact]
    public async Task ImpersonateTenant_NullDto_Rejected()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _, controller) = Build(db);

        var result = await controller.ImpersonateTenant(tenant.Id, null!);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task ImpersonateTenant_WithReason_Succeeds_AndAuditLogRecordsIt()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, admin, controller) = Build(db);

        var result = await controller.ImpersonateTenant(tenant.Id, new ImpersonateTenantDto("Kunde bat per Ticket #4711 um Hilfe bei einer Buchung"));

        Assert.IsType<OkObjectResult>(result);
        var entry = await db.AuditLogs.SingleAsync(a => a.TenantId == tenant.Id && a.Action == "tenant.impersonated");
        Assert.Contains("Ticket #4711", entry.Details);
        Assert.Contains(admin.Email, entry.Details);
    }
}
