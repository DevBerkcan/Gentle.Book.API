using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Configuration;
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

// "Preisrechner" link: SuperAdmin sets a per-studio HTTPS URL; the studio's admins read it
// (Professional + Agency only). Covers the plan gate on the read path, SuperAdmin-only writes,
// URL validation, downgrade/re-upgrade retention and tenant isolation.
public class PriceCalculatorTests
{
    private static (Tenant tenant, Subscription sub, TenantSettings settings) Seed(
        GentleBookDbContext db, SubscriptionPlan plan, string? url = null, string name = "Studio Preis")
    {
        var (tenant, sub) = AgencyTenantFactory.Seed(db, plan, name: name);
        var settings = new TenantSettings { TenantId = tenant.Id, CompanyName = tenant.Name, PriceCalculatorUrl = url };
        db.TenantSettings.Add(settings);
        db.SaveChanges();
        return (tenant, sub, settings);
    }

    private static SuperAdminController BuildSuperAdmin(GentleBookDbContext db, bool asSuperAdmin = true, Guid? tenantIdForNonSuperAdmin = null)
    {
        var user = asSuperAdmin
            ? ClaimsPrincipalFactory.SuperAdmin(Guid.NewGuid())
            : ClaimsPrincipalFactory.TenantAdmin(Guid.NewGuid(), tenantIdForNonSuperAdmin ?? Guid.NewGuid());
        var httpContext = new DefaultHttpContext { User = user };
        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        var audit = new AuditService(db, accessor, NullLogger<AuditService>.Instance);
        var emailService = TestServiceFactory.CreateEmailService(db);
        var mollieOptions = Options.Create(new MollieOptions());
        var mollieClient = new MollieClient(new HttpClient(), new StaticOptionsMonitor<MollieOptions>(new MollieOptions()));
        var mollieService = new MollieService(
            db, mollieClient, mollieOptions, new FakeBackgroundJobClient(), audit, emailService, NullLogger<MollieService>.Instance);
        return new SuperAdminController(
            db, TestConfiguration.Build(), emailService, NullLogger<SuperAdminController>.Instance,
            new JwtService(TestConfiguration.Build()), new FakeWebHostEnvironment(), audit, mollieService,
            new FakeBackgroundJobClient(), TestServiceFactory.CreateDataSubjectRequestService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    private static string? UrlOf(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return (string?)ok.Value!.GetType().GetProperty("url")!.GetValue(ok.Value);
    }

    // ── Gate / validation (pure logic) ────────────────────────────

    [Theory]
    [InlineData(SubscriptionPlan.Trial, false)]
    [InlineData(SubscriptionPlan.Starter, false)]
    [InlineData(SubscriptionPlan.Professional, true)]
    [InlineData(SubscriptionPlan.Agency, true)]
    public void Gate_OnlyProfessionalAndAgency(SubscriptionPlan plan, bool expected)
        => Assert.Equal(expected, PriceCalculatorGate.IsAllowed(plan));

    [Theory]
    [InlineData("http://preise.example.de")]
    [InlineData("javascript:alert(1)")]
    [InlineData("preise.example.de")]
    [InlineData("https://localhost")]
    [InlineData("https://user:pw@preise.example.de")]
    [InlineData("ftp://preise.example.de")]
    public void Normalize_RejectsInvalid(string input)
    {
        var (url, error) = PriceCalculatorGate.Normalize(input);
        Assert.Null(url);
        Assert.NotNull(error);
    }

    [Fact]
    public void Normalize_AcceptsHttpsAndTreatsBlankAsRemove()
    {
        Assert.Equal("https://preise.example.de/rechner?studio=1",
            PriceCalculatorGate.Normalize("  https://preise.example.de/rechner?studio=1 ").Url);
        Assert.Equal((null, null), PriceCalculatorGate.Normalize("   "));
        Assert.Equal((null, null), PriceCalculatorGate.Normalize(null));
    }

    // ── Studio read path ──────────────────────────────────────────

    [Theory]
    [InlineData(SubscriptionPlan.Professional)]
    [InlineData(SubscriptionPlan.Agency)]
    public async Task TenantRead_ProAndAgency_ReturnUrl(SubscriptionPlan plan)
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _, _) = Seed(db, plan, "https://preise.example.de/");
        var controller = TenantControllerFactory.Create(db, tenant.Id, "TenantAdmin");

        Assert.Equal("https://preise.example.de/", UrlOf(await controller.GetPriceCalculator()));
    }

    [Fact]
    public async Task TenantRead_ProWithoutUrl_ReturnsNull()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _, _) = Seed(db, SubscriptionPlan.Professional);
        var controller = TenantControllerFactory.Create(db, tenant.Id, "TenantAdmin");

        Assert.Null(UrlOf(await controller.GetPriceCalculator()));
    }

    [Theory]
    [InlineData(SubscriptionPlan.Starter)]
    [InlineData(SubscriptionPlan.Trial)]
    public async Task TenantRead_StarterAndTrial_Return402EvenWithStoredUrl(SubscriptionPlan plan)
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _, _) = Seed(db, plan, "https://preise.example.de/");
        var controller = TenantControllerFactory.Create(db, tenant.Id, "TenantAdmin");

        var result = Assert.IsType<ObjectResult>(await controller.GetPriceCalculator());
        Assert.Equal(402, result.StatusCode);
        Assert.DoesNotContain("preise.example.de", System.Text.Json.JsonSerializer.Serialize(result.Value));
    }

    [Fact]
    public async Task TenantRead_EmployeeRole_IsForbidden()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _, _) = Seed(db, SubscriptionPlan.Agency, "https://preise.example.de/");
        var controller = TenantControllerFactory.Create(db, tenant.Id, "Employee");

        Assert.IsType<ForbidResult>(await controller.GetPriceCalculator());
    }

    [Fact]
    public async Task Downgrade_HidesUrl_ReUpgrade_RestoresIt()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, sub, settings) = Seed(db, SubscriptionPlan.Professional, "https://preise.example.de/");

        sub.Plan = SubscriptionPlan.Starter;
        db.SaveChanges();
        var downgraded = Assert.IsType<ObjectResult>(await TenantControllerFactory.Create(db, tenant.Id, "TenantAdmin").GetPriceCalculator());
        Assert.Equal(402, downgraded.StatusCode);
        Assert.Equal("https://preise.example.de/", (await db.TenantSettings.FindAsync(settings.Id))!.PriceCalculatorUrl);

        sub.Plan = SubscriptionPlan.Agency;
        db.SaveChanges();
        Assert.Equal("https://preise.example.de/", UrlOf(await TenantControllerFactory.Create(db, tenant.Id, "TenantAdmin").GetPriceCalculator()));
    }

    [Fact]
    public async Task TenantRead_IsIsolatedBetweenStudios()
    {
        using var db = TestDbContextFactory.Create();
        var (tenantA, _, _) = Seed(db, SubscriptionPlan.Agency, "https://studio-a.example.de/", "Studio A");
        var (tenantB, _, _) = Seed(db, SubscriptionPlan.Professional, null, "Studio B");

        Assert.Equal("https://studio-a.example.de/", UrlOf(await TenantControllerFactory.Create(db, tenantA.Id, "TenantAdmin").GetPriceCalculator()));
        Assert.Null(UrlOf(await TenantControllerFactory.Create(db, tenantB.Id, "TenantAdmin").GetPriceCalculator()));
    }

    // ── SuperAdmin write path ─────────────────────────────────────

    [Fact]
    public async Task SuperAdmin_SetChangeRemove()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _, settings) = Seed(db, SubscriptionPlan.Professional);
        var sa = BuildSuperAdmin(db);

        Assert.Equal("https://preise.example.de/", UrlOf(await sa.SetTenantPriceCalculator(tenant.Id, new SetPriceCalculatorUrlDto("https://preise.example.de"))));
        Assert.Equal("https://neu.example.de/rechner", UrlOf(await sa.SetTenantPriceCalculator(tenant.Id, new SetPriceCalculatorUrlDto("https://neu.example.de/rechner"))));
        Assert.Null(UrlOf(await sa.SetTenantPriceCalculator(tenant.Id, new SetPriceCalculatorUrlDto(""))));
        Assert.Null((await db.TenantSettings.FindAsync(settings.Id))!.PriceCalculatorUrl);
    }

    [Fact]
    public async Task SuperAdmin_InvalidUrl_IsRejectedAndNothingStored()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _, settings) = Seed(db, SubscriptionPlan.Agency, "https://alt.example.de/");
        var sa = BuildSuperAdmin(db);

        Assert.IsType<BadRequestObjectResult>(await sa.SetTenantPriceCalculator(tenant.Id, new SetPriceCalculatorUrlDto("http://unsicher.example.de")));
        Assert.Equal("https://alt.example.de/", (await db.TenantSettings.FindAsync(settings.Id))!.PriceCalculatorUrl);
    }

    [Fact]
    public async Task SuperAdmin_WriteOnlyTouchesTargetStudio()
    {
        using var db = TestDbContextFactory.Create();
        var (tenantA, _, settingsA) = Seed(db, SubscriptionPlan.Agency, "https://studio-a.example.de/", "Studio A");
        var (tenantB, _, _) = Seed(db, SubscriptionPlan.Agency, null, "Studio B");

        await BuildSuperAdmin(db).SetTenantPriceCalculator(tenantB.Id, new SetPriceCalculatorUrlDto("https://studio-b.example.de"));

        Assert.Equal("https://studio-a.example.de/", (await db.TenantSettings.FindAsync(settingsA.Id))!.PriceCalculatorUrl);
    }

    [Fact]
    public async Task NonSuperAdmin_CannotWriteViaSuperAdminEndpoint()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _, settings) = Seed(db, SubscriptionPlan.Agency, "https://alt.example.de/");
        var controller = BuildSuperAdmin(db, asSuperAdmin: false, tenantIdForNonSuperAdmin: tenant.Id);

        Assert.IsType<ForbidResult>(await controller.SetTenantPriceCalculator(tenant.Id, new SetPriceCalculatorUrlDto("https://boese.example.de")));
        Assert.IsType<ForbidResult>(await controller.GetTenantPriceCalculator(tenant.Id));
        Assert.Equal("https://alt.example.de/", (await db.TenantSettings.FindAsync(settings.Id))!.PriceCalculatorUrl);
    }
}
