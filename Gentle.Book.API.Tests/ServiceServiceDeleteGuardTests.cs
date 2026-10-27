using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gentle.Book.API.Tests;

// Found during live pre-launch testing: three real past bookings (one completed, one no-show)
// vanished from the admin bookings list, dashboard and SuperAdmin entirely — not just
// unsearchable, genuinely gone — while the customer's own booking count/last-visit (denormalized
// counters, updated at booking time) and the audit log (a separate table) still referenced them.
// Root cause: DeleteServiceAsync only blocked deletion when the service had FUTURE non-cancelled
// bookings, but unconditionally hard-deleted every OTHER booking tied to it — past, completed,
// no-show — along with the service itself. Deleting a service an admin no longer offers should
// never be able to erase that a past appointment actually happened.
public class ServiceServiceDeleteGuardTests
{
    private static ServiceService Build(GentleBook.Api.Data.GentleBookDbContext db, Guid tenantId)
    {
        var tenantContext = new TenantContext();
        tenantContext.Set(tenantId, "test-tenant", isSuperAdmin: false, "TenantAdmin");
        return new ServiceService(db, NullLogger<ServiceService>.Instance, tenantContext);
    }

    [Fact]
    public async Task DeleteServiceAsync_WithPastCompletedBooking_ThrowsAndDoesNotDeleteEither()
    {
        using var db = TestDbContextFactory.Create();
        var tenant = new Tenant { Name = "Salon Delete", Slug = "salon-delete-" + Guid.NewGuid(), IsActive = true };
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
            BookingDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30)),
            StartTime = new TimeOnly(11, 0),
            EndTime = new TimeOnly(11, 30),
            Status = BookingStatus.Completed,
        };
        db.Bookings.Add(booking);
        db.SaveChanges();

        var serviceService = Build(db, tenant.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => serviceService.DeleteServiceAsync(service.Id));

        Assert.NotNull(await db.Services.FindAsync(service.Id));
        Assert.NotNull(await db.Bookings.FindAsync(booking.Id));
    }

    // The genuinely-zero-bookings success path can't be driven through DeleteServiceAsync in a
    // unit test — it opens a (non-serializable, but still a real) transaction right after the
    // guard above, which EF Core's InMemory provider doesn't support at all (same class of
    // limitation as BookingMonthlyLimitTests' CreateBookingAsync/CreateManualBookingAsync). The
    // guard itself — the part this bug fix is actually about — is fully covered above, since it
    // throws before that transaction is ever opened.
}
