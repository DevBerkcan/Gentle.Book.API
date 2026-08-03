using System.Security.Cryptography;
using System.Text;
using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Controllers;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gentle.Book.API.Tests;

/// <summary>
/// Covers the login/mustChangePassword bug: ResetPassword (the endpoint the setup/invite
/// link actually calls) previously never cleared PlatformUser.MustChangePassword, unlike
/// ChangePassword — leaving every admin who completed initial setup stuck being redirected
/// to the forced password-change screen on every subsequent login.
/// </summary>
public class AuthControllerTests
{
    private static AuthController CreateController(GentleBook.Api.Data.GentleBookDbContext db)
    {
        var jwt = new JwtService(TestConfiguration.Build());
        var email = TestServiceFactory.CreateEmailService(db);
        return new AuthController(db, jwt, email, NullLogger<AuthController>.Instance);
    }

    private static (Tenant tenant, PlatformUser admin) SeedActiveTenantWithAdmin(
        GentleBook.Api.Data.GentleBookDbContext db, bool mustChangePassword, bool isActive = true)
    {
        var tenant = new Tenant { Name = "Barber Wagner", Slug = "barber-wagner", IsActive = true };
        var subscription = new Subscription { TenantId = tenant.Id, Tenant = tenant }; // defaults to an active trial
        tenant.Subscription = subscription;

        var admin = new PlatformUser
        {
            TenantId = tenant.Id,
            Email = "admin@barber-wagner.test",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("CorrectHorse123"),
            FirstName = "Ada",
            LastName = "Admin",
            Role = PlatformRole.TenantAdmin,
            IsActive = isActive,
            MustChangePassword = mustChangePassword,
        };

        db.Tenants.Add(tenant);
        db.Subscriptions.Add(subscription);
        db.PlatformUsers.Add(admin);
        db.SaveChanges();

        return (tenant, admin);
    }

    [Fact]
    public async Task Login_AdminWithCompletedSetup_ReturnsMustChangePasswordFalse()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = SeedActiveTenantWithAdmin(db, mustChangePassword: false);
        var controller = CreateController(db);

        var result = await controller.Login(new TenantAdminLoginDto(tenant.Slug, "admin@barber-wagner.test", "CorrectHorse123"));

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.False((bool)GetProperty(ok.Value!, "mustChangePassword"));
    }

    [Fact]
    public async Task Login_AdminWithPendingPasswordChange_ReturnsMustChangePasswordTrue()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = SeedActiveTenantWithAdmin(db, mustChangePassword: true);
        var controller = CreateController(db);

        var result = await controller.Login(new TenantAdminLoginDto(tenant.Slug, "admin@barber-wagner.test", "CorrectHorse123"));

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.True((bool)GetProperty(ok.Value!, "mustChangePassword"));
    }

    [Fact]
    public async Task Login_DisabledUser_ReturnsUnauthorized()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = SeedActiveTenantWithAdmin(db, mustChangePassword: false, isActive: false);
        var controller = CreateController(db);

        var result = await controller.Login(new TenantAdminLoginDto(tenant.Slug, "admin@barber-wagner.test", "CorrectHorse123"));

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task Login_UnknownTenantSlug_ReturnsUnauthorized()
    {
        using var db = TestDbContextFactory.Create();
        var controller = CreateController(db);

        var result = await controller.Login(new TenantAdminLoginDto("does-not-exist", "nobody@nowhere.test", "whatever"));

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task ChangePassword_Success_ClearsMustChangePasswordFlag()
    {
        using var db = TestDbContextFactory.Create();
        var (_, admin) = SeedActiveTenantWithAdmin(db, mustChangePassword: true);
        var controller = CreateController(db);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = ClaimsPrincipalFactory.TenantAdmin(admin.Id, admin.TenantId!.Value) },
        };

        var result = await controller.ChangePassword(new ChangePasswordDto("CorrectHorse123", "NewCorrectHorse456"));

        Assert.IsType<OkObjectResult>(result);
        var reloaded = await db.PlatformUsers.FindAsync(admin.Id);
        Assert.False(reloaded!.MustChangePassword);
    }

    [Fact]
    public async Task ResetPassword_Success_ClearsMustChangePasswordFlag()
    {
        // Regression test for the actual reported bug: an admin who completes the mandatory
        // setup-link flow (which calls ResetPassword, not ChangePassword) must never be sent
        // back to the forced password-change screen on a later login.
        using var db = TestDbContextFactory.Create();
        var (_, admin) = SeedActiveTenantWithAdmin(db, mustChangePassword: true);
        var rawToken = "unit-test-raw-token";
        db.PasswordResetTokens.Add(new PasswordResetToken
        {
            Id = Guid.NewGuid(),
            UserId = admin.Id,
            TokenHash = Sha256Hex(rawToken),
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            IsUsed = false,
            CreatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        var controller = CreateController(db);

        var result = await controller.ResetPassword(new ResetPasswordDto(rawToken, "NewCorrectHorse456"));

        Assert.IsType<OkObjectResult>(result);
        var reloaded = await db.PlatformUsers.FindAsync(admin.Id);
        Assert.False(reloaded!.MustChangePassword);
    }

    [Fact]
    public async Task ResetPassword_ExpiredToken_ReturnsBadRequestAndLeavesFlagUntouched()
    {
        using var db = TestDbContextFactory.Create();
        var (_, admin) = SeedActiveTenantWithAdmin(db, mustChangePassword: true);
        var rawToken = "unit-test-expired-token";
        db.PasswordResetTokens.Add(new PasswordResetToken
        {
            Id = Guid.NewGuid(),
            UserId = admin.Id,
            TokenHash = Sha256Hex(rawToken),
            ExpiresAt = DateTime.UtcNow.AddHours(-1), // already expired
            IsUsed = false,
            CreatedAt = DateTime.UtcNow.AddHours(-2),
        });
        db.SaveChanges();
        var controller = CreateController(db);

        var result = await controller.ResetPassword(new ResetPasswordDto(rawToken, "NewCorrectHorse456"));

        Assert.IsType<BadRequestObjectResult>(result);
        var reloaded = await db.PlatformUsers.FindAsync(admin.Id);
        Assert.True(reloaded!.MustChangePassword);
    }

    [Fact]
    public async Task ActivateTrial_MissingRequiredAcceptance_DoesNotStartTrial()
    {
        using var db = TestDbContextFactory.Create();
        var controller = CreateController(db);

        var result = await controller.ActivateTrial(new ActivateTrialDto(
            "token", "Ada Admin", true, true, true, false, true));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(db.TrialAccessInvitations);
    }

    [Fact]
    public async Task ActivateTrial_AllAcceptances_StoresEvidenceButWaitsForManualRelease()
    {
        using var db = TestDbContextFactory.Create();
        const string rawToken = "trial-activation-token";
        var tenant = new Tenant { Name = "Salon Test", Slug = "salon-test", IsActive = false };
        var subscription = new Subscription
        {
            TenantId = tenant.Id,
            Tenant = tenant,
            Status = SubscriptionStatus.PendingAcceptance,
            Plan = SubscriptionPlan.Trial,
        };
        tenant.Subscription = subscription;
        var admin = new PlatformUser
        {
            TenantId = tenant.Id,
            Tenant = tenant,
            Email = "admin@salon-test.de",
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
            TokenHash = Sha256Hex(rawToken),
            ExpiresAt = DateTime.UtcNow.AddDays(1),
            TermsVersion = "terms-v1",
            PrivacyVersion = "privacy-v1",
            DpaVersion = "dpa-v1",
        };
        db.AddRange(tenant, subscription, admin, invitation);
        db.SaveChanges();

        var controller = CreateController(db);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        controller.HttpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("127.0.0.1");
        var result = await controller.ActivateTrial(new ActivateTrialDto(
            rawToken, "Ada Admin", true, true, true, true, true));

        Assert.IsType<OkObjectResult>(result);
        Assert.False(tenant.IsActive);
        Assert.False(admin.IsActive);
        Assert.Equal(SubscriptionStatus.PendingActivation, subscription.Status);
        Assert.Equal("Ada Admin", invitation.AcceptedByName);
        Assert.Equal("127.0.0.1", invitation.IpAddress);
        Assert.True(invitation.BusinessConfirmed && invitation.TermsAccepted && invitation.PrivacyAcknowledged);
        Assert.True(invitation.DpaAccepted && invitation.NoAutomaticPaidConversionAcknowledged);
        Assert.Empty(db.PasswordResetTokens);
    }

    private static string Sha256Hex(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    private static object GetProperty(object obj, string name) =>
        obj.GetType().GetProperty(name)!.GetValue(obj)!;
}
