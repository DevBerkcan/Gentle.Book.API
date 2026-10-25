using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.DTOs;
using GentleBook.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gentle.Book.API.Tests;

// Found during live pre-launch testing: re-confirming a cancelled booking (the admin bookings UI
// offers every status in its dropdown regardless of the booking's current status) always failed
// — Cancelled→Confirmed wasn't in the allowed state-machine transitions, only ever the reverse.
public class AdminServiceBookingStatusTransitionTests
{
    private static AdminService Build(GentleBook.Api.Data.GentleBookDbContext db) =>
        new(db, NullLogger<AdminService>.Instance, TestServiceFactory.CreateEmailService(db), new LoyaltyService(db, NullLogger<LoyaltyService>.Instance));

    private static (Tenant tenant, Booking booking) SeedCancelledBooking(GentleBook.Api.Data.GentleBookDbContext db)
    {
        var tenant = new Tenant { Name = "Salon Status", Slug = "salon-status-" + Guid.NewGuid(), IsActive = true };
        db.Tenants.Add(tenant);
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
            BookingDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)),
            StartTime = new TimeOnly(11, 0),
            EndTime = new TimeOnly(11, 30),
            Status = BookingStatus.Cancelled,
            CancelledAt = DateTime.UtcNow.AddHours(-2),
            CancellationReason = "Kunde hat abgesagt",
        };
        db.Bookings.Add(booking);
        db.SaveChanges();
        return (tenant, booking);
    }

    [Fact]
    public async Task UpdateBookingStatusAsync_CancelledToConfirmed_SucceedsAndClearsCancellationFields()
    {
        using var db = TestDbContextFactory.Create();
        var (_, booking) = SeedCancelledBooking(db);
        var service = Build(db);

        await service.UpdateBookingStatusAsync(booking.Id, new UpdateBookingStatusDto("Confirmed", null));

        var reloaded = await db.Bookings.FindAsync(booking.Id);
        Assert.Equal(BookingStatus.Confirmed, reloaded!.Status);
        Assert.Null(reloaded.CancelledAt);
        Assert.Null(reloaded.CancellationReason);
    }

    [Fact]
    public async Task UpdateBookingStatusAsync_CompletedToConfirmed_StillRejected()
    {
        // Sanity check the fix didn't loosen the state machine in general — Completed must stay
        // a true end-state, only Cancelled gained a way back to Confirmed.
        using var db = TestDbContextFactory.Create();
        var (_, booking) = SeedCancelledBooking(db);
        booking.Status = BookingStatus.Completed;
        booking.CancelledAt = null;
        db.SaveChanges();
        var service = Build(db);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdateBookingStatusAsync(booking.Id, new UpdateBookingStatusDto("Confirmed", null)));

        Assert.Contains("nicht zulässig", ex.Message);
    }
}
