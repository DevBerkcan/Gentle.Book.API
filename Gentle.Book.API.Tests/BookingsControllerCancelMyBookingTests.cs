using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Controllers;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gentle.Book.API.Tests;

// Found during live pre-launch testing: the /my-bookings customer portal called the staff-only
// DELETE /api/bookings/{id} (which requires a JWT the portal never has — it only has a
// CustomerPortalTokenService magic-link token) to cancel a booking. Every attempt silently
// 401'd with no error shown and the dialog stuck open. DELETE /api/bookings/my/{id} is the new,
// correctly-scoped endpoint: validates the portal token the same way GetMyBookings does, plus an
// ownership check so a token only ever cancels bookings under its own email.
public class BookingsControllerCancelMyBookingTests
{
    private static CustomerPortalTokenService TokenService() => new(TestConfiguration.Build());

    private static BookingsController CreateController(GentleBookDbContext db)
    {
        var bookingService = new BookingService(
            db, NullLogger<BookingService>.Instance, TestServiceFactory.CreateEmailService(db),
            new FakeBackgroundJobClient(), TestServiceFactory.CreateVoucherService(db));

        var controller = new BookingsController(
            bookingService, NullLogger<BookingsController>.Instance, TestServiceFactory.CreateEmailService(db),
            TestConfiguration.Build(), TokenService());

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity()) },
        };
        return controller;
    }

    private static Booking SeedConfirmedBooking(GentleBookDbContext db, Guid tenantId, string customerEmail)
    {
        var category = new ServiceCategory { TenantId = tenantId, Name = "Haarschnitt" };
        db.ServiceCategories.Add(category);
        var service = new Service { TenantId = tenantId, CategoryId = category.Id, Name = "Waschen, Schneiden, Föhnen", DurationMinutes = 30, Price = 40m, IsActive = true };
        db.Services.Add(service);
        var customer = new Customer { TenantId = tenantId, FirstName = "Kim", LastName = "Kunde", Email = customerEmail };
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
    public async Task CancelMyBooking_ValidTokenOwnBooking_CancelsIt()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = AgencyTenantFactory.Seed(db, SubscriptionPlan.Starter);
        var booking = SeedConfirmedBooking(db, tenant.Id, "kim@example.test");
        var controller = CreateController(db);
        var token = TokenService().CreateToken("kim@example.test");

        var result = await controller.CancelMyBooking(booking.Id, token, null);

        Assert.IsType<OkObjectResult>(result.Result);
        var reloaded = await db.Bookings.AsNoTracking().FirstAsync(b => b.Id == booking.Id);
        Assert.Equal(BookingStatus.Cancelled, reloaded.Status);
    }

    [Fact]
    public async Task CancelMyBooking_InvalidToken_Returns401AndDoesNotCancel()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = AgencyTenantFactory.Seed(db, SubscriptionPlan.Starter);
        var booking = SeedConfirmedBooking(db, tenant.Id, "kim@example.test");
        var controller = CreateController(db);

        var result = await controller.CancelMyBooking(booking.Id, "not-a-real-token", null);

        var objectResult = Assert.IsType<UnauthorizedObjectResult>(result.Result);
        var reloaded = await db.Bookings.AsNoTracking().FirstAsync(b => b.Id == booking.Id);
        Assert.Equal(BookingStatus.Confirmed, reloaded.Status);
    }

    [Fact]
    public async Task CancelMyBooking_TokenForDifferentEmail_ForbiddenAndDoesNotCancel()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = AgencyTenantFactory.Seed(db, SubscriptionPlan.Starter);
        var booking = SeedConfirmedBooking(db, tenant.Id, "kim@example.test");
        var controller = CreateController(db);
        var someoneElsesToken = TokenService().CreateToken("someone-else@example.test");

        var result = await controller.CancelMyBooking(booking.Id, someoneElsesToken, null);

        Assert.IsType<ForbidResult>(result.Result);
        var reloaded = await db.Bookings.AsNoTracking().FirstAsync(b => b.Id == booking.Id);
        Assert.Equal(BookingStatus.Confirmed, reloaded.Status);
    }
}
