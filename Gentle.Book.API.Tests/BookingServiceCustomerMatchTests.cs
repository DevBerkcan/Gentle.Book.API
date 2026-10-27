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

    // Found during further live pre-launch testing, right after the fix above shipped: the
    // confirmation page's "Gebucht von" showed the MATCHED customer's real stored name — not
    // the name actually typed on this booking — so booking with a known stranger's email leaked
    // that stranger's real name back to whoever typed it, and a legitimate case (a partner
    // booking under the same shared email) showed the wrong name too. The booking now
    // snapshots what was actually typed (Booking.ContactFirstName/LastName) and the response
    // prefers that over the customer's name.
    [Fact]
    public async Task GetBookingById_ContactNameDiffersFromMatchedCustomer_ResponseUsesContactName()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = AgencyTenantFactory.Seed(db, SubscriptionPlan.Starter);
        var category = new ServiceCategory { TenantId = tenant.Id, Name = "Haarschnitt" };
        db.ServiceCategories.Add(category);
        var service = new Service { TenantId = tenant.Id, CategoryId = category.Id, Name = "Waschen, Schneiden, Föhnen", DurationMinutes = 30, Price = 40m, IsActive = true };
        db.Services.Add(service);
        var existingCustomer = new Customer { TenantId = tenant.Id, FirstName = "TEST Claude-GoLive-Check", LastName = "Real", Email = "shared@example.test" };
        db.Customers.Add(existingCustomer);
        var booking = new Booking
        {
            TenantId = tenant.Id,
            CustomerId = existingCustomer.Id,
            ServiceId = service.Id,
            BookingDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)),
            StartTime = new TimeOnly(11, 0),
            EndTime = new TimeOnly(11, 30),
            Status = BookingStatus.Confirmed,
            ContactFirstName = "FREMD Umbenennungstest",
            ContactLastName = "Prober",
        };
        db.Bookings.Add(booking);
        db.SaveChanges();

        var bookingService = CreateService(db);
        var response = await bookingService.GetBookingByIdAsync(booking.Id);

        Assert.NotNull(response);
        Assert.Equal("FREMD Umbenennungstest", response!.Customer.FirstName);
        Assert.Equal("Prober", response.Customer.LastName);
    }

    [Fact]
    public async Task GetBookingById_NoContactNameSnapshot_FallsBackToCustomerName()
    {
        // Bookings created before this field existed have ContactFirstName/LastName == null —
        // must still show a name, not blank.
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = AgencyTenantFactory.Seed(db, SubscriptionPlan.Starter);
        var category = new ServiceCategory { TenantId = tenant.Id, Name = "Haarschnitt" };
        db.ServiceCategories.Add(category);
        var service = new Service { TenantId = tenant.Id, CategoryId = category.Id, Name = "Waschen, Schneiden, Föhnen", DurationMinutes = 30, Price = 40m, IsActive = true };
        db.Services.Add(service);
        var customer = new Customer { TenantId = tenant.Id, FirstName = "Kim", LastName = "Kunde", Email = "kim@example.test" };
        db.Customers.Add(customer);
        var booking = new Booking
        {
            TenantId = tenant.Id,
            CustomerId = customer.Id,
            ServiceId = service.Id,
            BookingDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)),
            StartTime = new TimeOnly(11, 0),
            EndTime = new TimeOnly(11, 30),
            Status = BookingStatus.Confirmed,
        };
        db.Bookings.Add(booking);
        db.SaveChanges();

        var bookingService = CreateService(db);
        var response = await bookingService.GetBookingByIdAsync(booking.Id);

        Assert.NotNull(response);
        Assert.Equal("Kim", response!.Customer.FirstName);
        Assert.Equal("Kunde", response.Customer.LastName);
    }
}
