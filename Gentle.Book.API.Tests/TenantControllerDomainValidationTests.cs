using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Controllers;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Options;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gentle.Book.API.Tests;

// Found during live pre-launch testing: requesting the custom domain "keine-domain" was accepted
// and saved as "PendingVerification" — Uri.CheckHostName alone only checks that a string is a
// DNS-legal single label, not that it's an actual fully-qualified domain with a real TLD.
public class TenantControllerDomainValidationTests
{
    private static (Tenant tenant, TenantController controller) Build(GentleBookDbContext db)
    {
        var tenant = new Tenant { Name = "Salon Domain-Test", Slug = "salon-domain-test-" + Guid.NewGuid(), IsActive = true };
        var subscription = new Subscription { TenantId = tenant.Id, Tenant = tenant, Plan = SubscriptionPlan.Agency, Status = SubscriptionStatus.Active };
        tenant.Subscription = subscription;
        var settings = new TenantSettings { TenantId = tenant.Id, CompanyName = tenant.Name };
        db.AddRange(tenant, subscription, settings);
        db.SaveChanges();

        var tenantContext = new TenantContext();
        tenantContext.Set(tenant.Id, tenant.Slug, role: "TenantAdmin");
        var httpContext = new DefaultHttpContext { User = ClaimsPrincipalFactory.TenantAdmin(Guid.NewGuid(), tenant.Id) };
        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        var audit = new AuditService(db, accessor, NullLogger<AuditService>.Instance);
        var emailService = TestServiceFactory.CreateEmailService(db);
        var mollieOptions = Options.Create(new MollieOptions());
        var mollieClient = new MollieClient(new HttpClient(), new StaticOptionsMonitor<MollieOptions>(new MollieOptions()));
        var mollieService = new MollieService(
            db, mollieClient, mollieOptions, new FakeBackgroundJobClient(), audit, emailService, NullLogger<MollieService>.Instance);
        var controller = new TenantController(
            db, tenantContext, emailService, NullLogger<TenantController>.Instance, new FakeWebHostEnvironment(),
            audit, mollieService, mollieOptions, new ApiKeyService(db), TestServiceFactory.CreateDataSubjectRequestService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
        return (tenant, controller);
    }

    [Theory]
    [InlineData("keine-domain")]
    [InlineData("localhost")]
    [InlineData("just-a-label")]
    [InlineData("")]
    [InlineData("127.0.0.1")]
    public async Task UpdateDomain_NotAnFqdn_Rejected(string domain)
    {
        using var db = TestDbContextFactory.Create();
        var (_, controller) = Build(db);

        var result = await controller.UpdateDomain(new UpdateCustomDomainDto(domain));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Theory]
    [InlineData("buchung.deinefirma.de")]
    [InlineData("termine.hautliebe-laser.de")]
    [InlineData("book.example.com")]
    public async Task UpdateDomain_ValidFqdn_Accepted(string domain)
    {
        using var db = TestDbContextFactory.Create();
        var (_, controller) = Build(db);

        var result = await controller.UpdateDomain(new UpdateCustomDomainDto(domain));

        Assert.IsType<OkObjectResult>(result);
    }
}
