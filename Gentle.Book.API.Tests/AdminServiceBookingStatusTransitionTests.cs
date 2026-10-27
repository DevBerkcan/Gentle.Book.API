using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.DTOs;
using GentleBook.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gentle.Book.API.Tests;

// Found during live pre-launch testing: re-confirming a cancelled booking (the admin bookings UI
// offers every status in its dropdown regardless of the booking's current status) always failed
// — Cancelled→Confirmed wasn't in the allowed state-machine transitions, only ever the reverse.
public class AdminServiceBookingStatusTransitionTests
{
    private static AdminService Build(GentleBook.Api.Data.GentleBookDbContext db) =>
        new(db, NullLogger<AdminService>.Instance, TestServiceFactory.CreateEmailService(db), new LoyaltyService(db, NullLogger<LoyaltyService>.Instance), TestServiceFactory.CreateVoucherService(db), new FakeBackgroundJobClient());

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

    private static (Tenant tenant, Customer customer, Booking booking) SeedConfirmedBooking(
        GentleBook.Api.Data.GentleBookDbContext db, DateTime? existingLastVisit = null)
    {
        var tenant = new Tenant { Name = "Salon Status", Slug = "salon-status-" + Guid.NewGuid(), IsActive = true };
        db.Tenants.Add(tenant);
        var category = new ServiceCategory { TenantId = tenant.Id, Name = "Haarschnitt" };
        db.ServiceCategories.Add(category);
        var service = new Service { TenantId = tenant.Id, CategoryId = category.Id, Name = "Waschen, Schneiden, Föhnen", DurationMinutes = 30, Price = 40m, IsActive = true };
        db.Services.Add(service);
        var customer = new Customer { TenantId = tenant.Id, FirstName = "Kim", LastName = "Kunde", LastVisit = existingLastVisit };
        db.Customers.Add(customer);
        var booking = new Booking
        {
            TenantId = tenant.Id,
            CustomerId = customer.Id,
            ServiceId = service.Id,
            BookingDate = DateOnly.FromDateTime(DateTime.UtcNow),
            StartTime = new TimeOnly(11, 0),
            EndTime = new TimeOnly(11, 30),
            Status = BookingStatus.Confirmed,
        };
        db.Bookings.Add(booking);
        db.SaveChanges();
        return (tenant, customer, booking);
    }

    // Found during live pre-launch testing: "Letzter Besuch" stayed "—" even after a booking was
    // manually marked Completed. BookingCompletionService.AutoCompleteBookingsAsync (the only
    // other place LastVisit gets set after creation) only runs for Agency tenants, so every
    // other plan's staff marking a booking Completed by hand never advanced it at all.
    [Fact]
    public async Task UpdateBookingStatusAsync_ConfirmedToCompleted_SetsCustomerLastVisit()
    {
        using var db = TestDbContextFactory.Create();
        var (_, customer, booking) = SeedConfirmedBooking(db);
        var adminService = Build(db);

        await adminService.UpdateBookingStatusAsync(booking.Id, new UpdateBookingStatusDto("Completed", null));

        var reloadedCustomer = await db.Customers.AsNoTracking().FirstAsync(c => c.Id == customer.Id);
        Assert.NotNull(reloadedCustomer.LastVisit);
        Assert.Equal(booking.BookingDate.ToDateTime(booking.StartTime), reloadedCustomer.LastVisit!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task UpdateBookingStatusAsync_ConfirmedToCompleted_ExistingLastVisitIsMoreRecent_DoesNotMoveItBackwards()
    {
        using var db = TestDbContextFactory.Create();
        var alreadyNewer = DateTime.UtcNow.AddDays(1);
        var (_, customer, booking) = SeedConfirmedBooking(db, existingLastVisit: alreadyNewer);
        var adminService = Build(db);

        await adminService.UpdateBookingStatusAsync(booking.Id, new UpdateBookingStatusDto("Completed", null));

        var reloadedCustomer = await db.Customers.AsNoTracking().FirstAsync(c => c.Id == customer.Id);
        Assert.Equal(alreadyNewer, reloadedCustomer.LastVisit!.Value, TimeSpan.FromSeconds(1));
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

    // Found during live pre-launch testing (employee test account): the bookings UI offered
    // these transitions in its dropdown and they appeared to just silently fail to save — this
    // confirms the backend has always correctly rejected them (Completed is a true end-state);
    // the actual bug was the frontend not disabling/explaining the rejected options, fixed
    // separately in app/admin/bookings/page.tsx.
    [Theory]
    [InlineData("Pending")]
    [InlineData("Cancelled")]
    public async Task UpdateBookingStatusAsync_CompletedToOtherStatus_Rejected(string targetStatus)
    {
        using var db = TestDbContextFactory.Create();
        var (_, booking) = SeedCancelledBooking(db);
        booking.Status = BookingStatus.Completed;
        booking.CancelledAt = null;
        db.SaveChanges();
        var service = Build(db);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdateBookingStatusAsync(booking.Id, new UpdateBookingStatusDto(targetStatus, null)));

        Assert.Contains("nicht zulässig", ex.Message);

        var reloaded = await db.Bookings.AsNoTracking().FirstAsync(b => b.Id == booking.Id);
        Assert.Equal(BookingStatus.Completed, reloaded.Status);
    }
}
