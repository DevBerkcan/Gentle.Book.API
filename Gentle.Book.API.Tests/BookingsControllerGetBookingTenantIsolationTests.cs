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
/// GetBooking is [AllowAnonymous] (the public booking-confirmation page has no tenant context
/// to check), so GentleBookDbContext's global tenant query filter is a no-op there by design —
/// see TenantIsolationTests. These tests cover the defense-in-depth fix added alongside this
/// suite: an *authenticated* caller (TenantAdmin/LocationAdmin/Employee) must still not be able
/// to view another tenant's booking by guessing or leaking its GUID, while the truly anonymous
/// public-confirmation path keeps working unchanged.
/// </summary>
public class BookingsControllerGetBookingTenantIsolationTests
{
    private static BookingsController CreateController(GentleBook.Api.Data.GentleBookDbContext db, System.Security.Claims.ClaimsPrincipal user)
    {
        var bookingService = new BookingService(
            db, NullLogger<BookingService>.Instance, TestServiceFactory.CreateEmailService(db),
            new FakeBackgroundJobClient(), TestServiceFactory.CreateVoucherService(db));

        var controller = new BookingsController(
            bookingService, NullLogger<BookingsController>.Instance, TestServiceFactory.CreateEmailService(db),
            TestConfiguration.Build(), new CustomerPortalTokenService(TestConfiguration.Build()));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user },
        };
        return controller;
    }

    private static Booking SeedBooking(GentleBook.Api.Data.GentleBookDbContext db, Guid tenantId)
    {
        var category = new ServiceCategory { TenantId = tenantId, Name = "Haarschnitt" };
        db.ServiceCategories.Add(category);
        var service = new Service { TenantId = tenantId, CategoryId = category.Id, Name = "Waschen, Schneiden, Föhnen", DurationMinutes = 30, Price = 40m, IsActive = true };
        db.Services.Add(service);
        var customer = new Customer { TenantId = tenantId, FirstName = "Kim", LastName = "Kunde", Email = "kim@example.test" };
        db.Customers.Add(customer);
        var booking = new Booking
        {
            TenantId = tenantId,
            CustomerId = customer.Id,
            ServiceId = service.Id,
            BookingDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)),
            StartTime = new TimeOnly(11, 0),
            EndTime = new TimeOnly(11, 30),
            Status = BookingStatus.Confirmed,
            CreatedAt = DateTime.UtcNow,
        };
        db.Bookings.Add(booking);
        db.SaveChanges();
        return booking;
    }

    [Fact]
    public async Task GetBooking_AsTenantAdminOfDifferentTenant_IsForbidden()
    {
        using var db = TestDbContextFactory.Create();
        var (ownerTenant, _) = AgencyTenantFactory.Seed(db, SubscriptionPlan.Starter, name: "Owner Salon");
        var (otherTenant, _) = AgencyTenantFactory.Seed(db, SubscriptionPlan.Starter, name: "Other Salon");
        var booking = SeedBooking(db, ownerTenant.Id);

        var controller = CreateController(db, ClaimsPrincipalFactory.TenantAdmin(Guid.NewGuid(), otherTenant.Id));

        var result = await controller.GetBooking(booking.Id);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task GetBooking_AsTenantAdminOfOwnTenant_ReturnsOk()
    {
        using var db = TestDbContextFactory.Create();
        var (ownerTenant, _) = AgencyTenantFactory.Seed(db, SubscriptionPlan.Starter, name: "Owner Salon");
        var booking = SeedBooking(db, ownerTenant.Id);

        var controller = CreateController(db, ClaimsPrincipalFactory.TenantAdmin(Guid.NewGuid(), ownerTenant.Id));

        var result = await controller.GetBooking(booking.Id);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetBooking_Anonymous_StillReturnsOk()
    {
        // The public booking-confirmation page has no auth/tenant context at all — this must
        // keep working exactly as before; the new tenant check only applies to authenticated callers.
        using var db = TestDbContextFactory.Create();
        var (ownerTenant, _) = AgencyTenantFactory.Seed(db, SubscriptionPlan.Starter, name: "Owner Salon");
        var booking = SeedBooking(db, ownerTenant.Id);

        var controller = CreateController(db, new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity()));

        var result = await controller.GetBooking(booking.Id);

        Assert.IsType<OkObjectResult>(result.Result);
    }
}
