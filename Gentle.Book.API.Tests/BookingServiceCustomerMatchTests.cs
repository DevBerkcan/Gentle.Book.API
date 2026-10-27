using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.DTOs;
using GentleBook.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gentle.Book.API.Tests;

// Found during live pre-launch testing: the public, [AllowAnonymous] booking endpoint matched
// an existing customer by email/phone and then unconditionally overwrote their FirstName/
// LastName with whatever the (unverified) booker typed — retroactively renaming that customer
// across every past booking and activity. Anyone who knew (or guessed) an existing customer's
// email could do this. FindOrCreateCustomerForPublicBookingAsync is the extracted, directly
// testable piece of CreateBookingAsync that fixes this (CreateBookingAsync itself can't be
// driven end-to-end in a unit test — see BookingMonthlyLimitTests for why).
public class BookingServiceCustomerMatchTests
{
    private static BookingService CreateService(GentleBook.Api.Data.GentleBookDbContext db) =>
        new(db, NullLogger<BookingService>.Instance, TestServiceFactory.CreateEmailService(db),
            new FakeBackgroundJobClient(), TestServiceFactory.CreateVoucherService(db));

    [Fact]
    public async Task FindOrCreateCustomer_MatchByEmail_DoesNotOverwriteExistingName()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = AgencyTenantFactory.Seed(db, SubscriptionPlan.Starter);
        var existing = new Customer
        {
            TenantId = tenant.Id,
            FirstName = "Kim",
            LastName = "Kunde",
            Email = "kim@example.test",
        };
        db.Customers.Add(existing);
        db.SaveChanges();

        var service = CreateService(db);
        var result = await service.FindOrCreateCustomerForPublicBookingAsync(
            tenant.Id,
            new CustomerInfoDto("TEST Claude-GoLive-Check", "Hacker", "kim@example.test", null),
            null);

        Assert.Equal(existing.Id, result.Id);
        Assert.Equal("Kim", result.FirstName);
        Assert.Equal("Kunde", result.LastName);
    }

    [Fact]
    public async Task FindOrCreateCustomer_MatchByPhone_DoesNotOverwriteExistingName()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = AgencyTenantFactory.Seed(db, SubscriptionPlan.Starter);
        var existing = new Customer
        {
            TenantId = tenant.Id,
            FirstName = "Kim",
            LastName = "Kunde",
            Phone = "+49151234567",
        };
        db.Customers.Add(existing);
        db.SaveChanges();

        var service = CreateService(db);
        var result = await service.FindOrCreateCustomerForPublicBookingAsync(
            tenant.Id,
            new CustomerInfoDto("Someone", "Else", null, "+49151234567"),
            null);

        Assert.Equal(existing.Id, result.Id);
        Assert.Equal("Kim", result.FirstName);
        Assert.Equal("Kunde", result.LastName);
    }

    [Fact]
    public async Task FindOrCreateCustomer_MatchByEmail_BackfillsMissingPhoneOnly()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = AgencyTenantFactory.Seed(db, SubscriptionPlan.Starter);
        var existing = new Customer
        {
            TenantId = tenant.Id,
            FirstName = "Kim",
            LastName = "Kunde",
            Email = "kim@example.test",
            Phone = null,
        };
        db.Customers.Add(existing);
        db.SaveChanges();

        var service = CreateService(db);
        var result = await service.FindOrCreateCustomerForPublicBookingAsync(
            tenant.Id,
            new CustomerInfoDto("Kim", "Kunde", "kim@example.test", "+49151234567"),
            null);

        Assert.Equal("+49151234567", result.Phone);
    }

    [Fact]
    public async Task FindOrCreateCustomer_NoMatch_CreatesNewCustomerWithGivenName()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = AgencyTenantFactory.Seed(db, SubscriptionPlan.Starter);

        var service = CreateService(db);
        var result = await service.FindOrCreateCustomerForPublicBookingAsync(
            tenant.Id,
            new CustomerInfoDto("Neu", "Kunde", "neu@example.test", null),
            null);
        await db.SaveChangesAsync();

        Assert.Equal("Neu", result.FirstName);
        Assert.Equal("Kunde", result.LastName);
        Assert.Single(db.Customers, c => c.TenantId == tenant.Id);
    }
}
