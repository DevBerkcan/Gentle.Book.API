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

public class ManualTrialActivationTests
{
    [Fact]
    public async Task ActivatePreparedTrial_AfterLegalAcceptance_StartsTrialAndStoresActivator()
    {
        using var db = TestDbContextFactory.Create();
        var tenant = new Tenant { Name = "Salon Freigabe", Slug = "salon-freigabe", IsActive = false };
        var subscription = new Subscription
        {
            TenantId = tenant.Id,
            Tenant = tenant,
            Status = SubscriptionStatus.PendingActivation,
            Plan = SubscriptionPlan.Trial,
        };
        tenant.Subscription = subscription;
        var admin = new PlatformUser
        {
            TenantId = tenant.Id,
            Tenant = tenant,
            Email = "admin@salon-freigabe.de",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Locked123!"),
            FirstName = "Ada",
            LastName = "Admin",
            Role = PlatformRole.TenantAdmin,
            IsActive = false,
        };
        var invitation = new TrialAccessInvitation
        {
            TenantId = tenant.Id,
            Tenant = tenant,
            UserId = admin.Id,
            User = admin,
            Email = admin.Email,
            TokenHash = "accepted",
            ExpiresAt = DateTime.UtcNow.AddDays(1),
            AcceptedAt = DateTime.UtcNow.AddMinutes(-5),
            AcceptedByName = "Ada Admin",
            TermsVersion = "terms-v1",
            PrivacyVersion = "privacy-v1",
            DpaVersion = "dpa-v1",
            BusinessConfirmed = true,
            TermsAccepted = true,
            PrivacyAcknowledged = true,
            DpaAccepted = true,
            NoAutomaticPaidConversionAcknowledged = true,
        };
        db.AddRange(tenant, subscription, admin, invitation);
        db.SaveChanges();

        var superAdminId = Guid.NewGuid();
        var httpContext = new DefaultHttpContext { User = ClaimsPrincipalFactory.SuperAdmin(superAdminId) };
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
            mollieService)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
        var before = DateTime.UtcNow;

        var result = await controller.ActivatePreparedTrial(tenant.Id);

        Assert.IsType<OkObjectResult>(result);
        Assert.True(tenant.IsActive);
        Assert.True(admin.IsActive);
        Assert.Equal(SubscriptionStatus.Trial, subscription.Status);
        Assert.Equal(superAdminId, subscription.TrialActivatedByUserId);
        Assert.InRange(subscription.TrialStartedAt, before, DateTime.UtcNow);
        Assert.InRange(subscription.TrialEndsAt, before.AddDays(14), DateTime.UtcNow.AddDays(14));
        Assert.Single(db.PasswordResetTokens);
        Assert.Contains(db.AuditLogs, entry => entry.Action == "subscription.trial_activated");
    }
}
