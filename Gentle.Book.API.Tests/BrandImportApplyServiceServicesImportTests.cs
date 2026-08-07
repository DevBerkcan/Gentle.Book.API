// Gentle.Book.API.Tests/BrandImportApplyServiceServicesImportTests.cs
// Covers the new "Services" import path of Brand Import (ApplyBrandProposalOptions.ApplyServices):
// detected treatments must be capped at the tenant's remaining PlanLimits.MaxServices quota
// instead of throwing away a partially-successful import once the limit is hit mid-loop.
using System.Text.Json;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services;
using GentleBook.Api.Services.BrandImport;
using Gentle.Book.API.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gentle.Book.API.Tests;

public class BrandImportApplyServiceServicesImportTests
{
    private sealed class ThrowingSafeWebsiteFetcher : ISafeWebsiteFetcher
    {
        // ApplyServices never downloads anything — only ApplyLogo does — so any call here means
        // the test wired something up wrong.
        public Task<FetchResult> FetchAsync(Uri url, IReadOnlyCollection<string> allowedContentTypePrefixes, long maxBytes, int maxRedirects, TimeSpan timeout, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not expected to be called for a Services-only apply.");
    }

    private static (DbContextOptions<GentleBookDbContext> Options, Guid TenantId, Guid ResultId, Guid ProposalId) Seed(
        SubscriptionPlan plan, int existingServiceCount, List<DetectedServiceDto> detectedServices)
    {
        var options = new DbContextOptionsBuilder<GentleBookDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var tenantId = Guid.NewGuid();
        var resultId = Guid.NewGuid();
        var proposalId = Guid.NewGuid();

        using var seed = new GentleBookDbContext(options, tenantContext: null);

        var tenant = new Tenant { Id = tenantId, Name = "Brand Import Test Tenant", Slug = $"bi-test-{tenantId:N}", IsActive = true };
        seed.Tenants.Add(tenant);
        seed.Subscriptions.Add(new Subscription { TenantId = tenantId, Tenant = tenant, Plan = plan, Status = SubscriptionStatus.Active });
        seed.TenantSettings.Add(new TenantSettings { TenantId = tenantId, CompanyName = "Brand Import Test Tenant", DefaultCurrency = "EUR" });

        var existingCategory = new ServiceCategory { TenantId = tenantId, Name = "Bestehend", DisplayOrder = 0 };
        seed.ServiceCategories.Add(existingCategory);
        for (var i = 0; i < existingServiceCount; i++)
        {
            seed.Services.Add(new Service
            {
                TenantId = tenantId,
                CategoryId = existingCategory.Id,
                Name = $"Bestehender Service {i}",
                DurationMinutes = 30,
                Price = 10,
                Currency = "EUR",
                DisplayOrder = i,
                IsActive = true,
            });
        }

        var contentJson = JsonSerializer.Serialize(new ExtractedContent { Services = detectedServices });
        seed.BrandImportResults.Add(new BrandImportResult { Id = resultId, TenantId = tenantId, JobId = Guid.NewGuid(), WebsiteTitle = "Test" });
        seed.BrandThemeProposals.Add(new BrandThemeProposal
        {
            Id = proposalId,
            TenantId = tenantId,
            ImportResultId = resultId,
            ProposalKey = "original",
            Name = "Originalgetreu",
            TemplateId = "classic",
            ThemeJson = JsonSerializer.Serialize(new
            {
                Background = "#FFFFFF", Surface = "#FFFFFF", Primary = "#000000", PrimaryForeground = "#FFFFFF",
                Secondary = "#EEEEEE", Accent = "#999999", Text = "#000000", TextMuted = "#666666", Border = "#DDDDDD",
                HeadingFontKey = "inter", BodyFontKey = "inter", CardRadiusPx = "16", ButtonRadiusPx = "12",
                ButtonStyle = "rounded", CardStyle = "outlined", AnimationSpeed = "none", TemplateId = "classic",
            }),
            ContentJson = contentJson,
        });

        seed.SaveChanges();
        return (options, tenantId, resultId, proposalId);
    }

    private static (BrandImportApplyService Apply, GentleBookDbContext Db) BuildSut(DbContextOptions<GentleBookDbContext> options, Guid tenantId)
    {
        var tenantContext = new TenantContext();
        tenantContext.Set(tenantId, "bi-test", isSuperAdmin: false, role: "TenantAdmin");

        var db = new GentleBookDbContext(options, tenantContext);
        var serviceService = new ServiceService(db, NullLogger<ServiceService>.Instance, tenantContext);
        var apply = new BrandImportApplyService(db, new ThrowingSafeWebsiteFetcher(), new FakeWebHostEnvironment(), serviceService, NullLogger<BrandImportApplyService>.Instance);
        return (apply, db);
    }

    private static ApplyBrandProposalOptions ServicesOnly() =>
        new(ApplyLogo: false, ApplyColors: false, ApplyTypography: false, ApplyDescription: false, ApplySocialLinks: false,
            SelectedLogoAssetId: null, ApplyServices: true);

    [Fact]
    public async Task ApplyServices_ImportsAllDetectedServices_WhenUnderPlanLimit()
    {
        var detected = new List<DetectedServiceDto>
        {
            new("Hyaluron Lippen", 249m, "CHF", 30),
            new("Botox Zornesfalte", 150m, "CHF", 20),
            new("HIFU Hals", null, null, null), // no price/duration on the source page
        };
        var (options, tenantId, resultId, proposalId) = Seed(SubscriptionPlan.Professional, existingServiceCount: 0, detected);
        var (apply, db) = BuildSut(options, tenantId);

        var result = await apply.ApplyAsync(tenantId, resultId, proposalId, ServicesOnly(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(3, result.ImportedServicesCount);
        Assert.Equal(0, result.SkippedServicesCount);

        var created = await db.Services.Where(s => s.TenantId == tenantId).ToListAsync();
        Assert.Equal(3, created.Count);
        Assert.Contains(created, s => s.Name == "Hyaluron Lippen" && s.Price == 249m && s.Currency == "CHF" && s.DurationMinutes == 30);
        // Missing price/duration must not be dropped — they get a safe default instead.
        Assert.Contains(created, s => s.Name == "HIFU Hals" && s.Price == 0m && s.DurationMinutes == 30);
    }

    [Fact]
    public async Task ApplyServices_CapsImport_WhenExceedingPlanLimit()
    {
        // Starter plan allows 15 services (Configuration/PlanLimits.cs); pre-fill 14 so only one
        // more detected service fits.
        var detected = new List<DetectedServiceDto>
        {
            new("Service A", 10m, "EUR", 30),
            new("Service B", 20m, "EUR", 30),
            new("Service C", 30m, "EUR", 30),
        };
        var (options, tenantId, resultId, proposalId) = Seed(SubscriptionPlan.Starter, existingServiceCount: 14, detected);
        var (apply, db) = BuildSut(options, tenantId);

        var result = await apply.ApplyAsync(tenantId, resultId, proposalId, ServicesOnly(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, result.ImportedServicesCount);
        Assert.Equal(2, result.SkippedServicesCount);

        var totalActive = await db.Services.CountAsync(s => s.TenantId == tenantId && s.IsActive);
        Assert.Equal(15, totalActive);
    }

    [Fact]
    public async Task ApplyServices_SkipsDetectedServiceThatAlreadyExistsByName()
    {
        var detected = new List<DetectedServiceDto> { new("Bestehender Service 0", 99m, "EUR", 45) };
        var (options, tenantId, resultId, proposalId) = Seed(SubscriptionPlan.Professional, existingServiceCount: 1, detected);
        var (apply, db) = BuildSut(options, tenantId);

        var result = await apply.ApplyAsync(tenantId, resultId, proposalId, ServicesOnly(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, result.ImportedServicesCount);
        var totalActive = await db.Services.CountAsync(s => s.TenantId == tenantId && s.IsActive);
        Assert.Equal(1, totalActive); // still just the pre-existing one — no duplicate created
    }
}
