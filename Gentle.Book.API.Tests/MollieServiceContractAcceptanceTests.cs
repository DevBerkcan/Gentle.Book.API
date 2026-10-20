using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Options;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gentle.Book.API.Tests;

// Covers the archiving BLOCKER from LEGAL_AUDIT_2026-09-06.md: "Rechtsfassungen nicht
// unveränderlich zum einzelnen Standardcheckout archiviert" — StartMandateFlowAsync must (a)
// refuse to proceed at all ("fail closed") until a released LegalDocument exists for Terms,
// Privacy and Dpa, and (b) write an immutable ContractAcceptance snapshot (which document
// versions, which plan/price/interval, who, when) before ever calling Mollie.
public class MollieServiceContractAcceptanceTests
{
    private static MollieService BuildMollieService(GentleBook.Api.Data.GentleBookDbContext db, Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var httpContextAccessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var auditService = new AuditService(db, httpContextAccessor, NullLogger<AuditService>.Instance);
        var emailService = TestServiceFactory.CreateEmailService(db);
        var mollieOptions = Options.Create(new MollieOptions { WebhookUrl = "https://api.example.test/webhooks/mollie", RedirectUrlBase = "https://app.example.test" });
        var mollieClient = new MollieClient(
            new HttpClient(new FakeHttpMessageHandler(responder)) { BaseAddress = new Uri("https://api.mollie.com/v2/") },
            new StaticOptionsMonitor<MollieOptions>(new MollieOptions { ApiKey = "test_x" }));
        return new MollieService(
            db, mollieClient, mollieOptions, new FakeBackgroundJobClient(), auditService, emailService, NullLogger<MollieService>.Instance);
    }

    private static (Tenant tenant, Subscription subscription) SeedEligibleSubscription(GentleBook.Api.Data.GentleBookDbContext db)
    {
        var tenant = new Tenant { Name = "Salon Vertragsnachweis", Slug = "salon-vertragsnachweis-" + Guid.NewGuid(), IsActive = true };
        var settings = new TenantSettings
        {
            TenantId = tenant.Id,
            CompanyName = "Salon Vertragsnachweis",
            LegalCompanyName = "Salon Vertragsnachweis GmbH",
            BillingStreet = "Musterstraße 1",
            BillingZipCode = "12345",
            BillingCity = "Musterstadt",
            BillingCountry = "DE",
            Email = "billing@salon-vertragsnachweis.de",
        };
        var subscription = new Subscription
        {
            TenantId = tenant.Id,
            Tenant = tenant,
            Plan = SubscriptionPlan.Starter,
            Interval = SubscriptionInterval.Monthly,
            Status = SubscriptionStatus.Trial,
        };
        tenant.Subscription = subscription;
        tenant.Settings = settings;
        db.AddRange(tenant, settings, subscription);
        db.SaveChanges();
        return (tenant, subscription);
    }

    private static void SeedReleasedLegalDocuments(GentleBook.Api.Data.GentleBookDbContext db)
    {
        foreach (var type in new[] { LegalDocumentType.Terms, LegalDocumentType.Privacy, LegalDocumentType.Dpa })
        {
            db.LegalDocuments.Add(new LegalDocument { Type = type, Version = "test-1", Language = "de", FullText = $"Test {type}.", Sha256Hash = "test" });
        }
        db.SaveChanges();
    }

    private static HttpResponseMessage Json(string body) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
    };

    [Fact]
    public async Task StartMandateFlowAsync_NoLegalDocumentsReleased_FailsClosedAndNeverCallsMollie()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = SeedEligibleSubscription(db);
        // Deliberately not calling SeedReleasedLegalDocuments.

        var mollieWasCalled = false;
        var mollieService = BuildMollieService(db, _ => { mollieWasCalled = true; return Json("{}"); });

        var result = await mollieService.StartMandateFlowAsync(tenant.Id, "Starter", "Monthly", Guid.NewGuid(), true);

        Assert.False(result.Success);
        Assert.False(mollieWasCalled);
        Assert.Empty(db.ContractAcceptances);
    }

    [Fact]
    public async Task StartMandateFlowAsync_LegalDocumentsReleased_RecordsAcceptanceSnapshotBeforeCallingMollie()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, subscription) = SeedEligibleSubscription(db);
        SeedReleasedLegalDocuments(db);
        var actorId = Guid.NewGuid();

        var mollieService = BuildMollieService(db, req =>
        {
            if (req.RequestUri!.ToString().Contains("/customers"))
                return Json("{\"id\":\"cst_test\"}");
            return Json("{\"id\":\"tr_test\",\"status\":\"open\",\"_links\":{\"checkout\":{\"href\":\"https://mollie.test/checkout\"}}}");
        });

        var result = await mollieService.StartMandateFlowAsync(tenant.Id, "Starter", "Monthly", actorId, true);

        Assert.True(result.Success, result.Error);
        var acceptance = Assert.Single(db.ContractAcceptances);
        Assert.Equal(tenant.Id, acceptance.TenantId);
        Assert.Equal(subscription.Id, acceptance.SubscriptionId);
        Assert.Equal(actorId, acceptance.ActorUserId);
        Assert.True(acceptance.BusinessConfirmed);
        Assert.Equal("Starter", acceptance.Plan);
        Assert.Equal("Monthly", acceptance.Interval);
        Assert.Equal(29m, acceptance.PriceSnapshot); // PlanLimits.Starter.MonthlyPrice
        var terms = db.LegalDocuments.Single(d => d.Id == acceptance.TermsDocumentId);
        Assert.Equal(LegalDocumentType.Terms, terms.Type);
    }
}
