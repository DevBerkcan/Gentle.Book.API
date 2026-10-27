using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gentle.Book.API.Tests;

// Found during live pre-launch testing: ServiceService.DeleteServiceAsync used to hard-delete a
// service's entire booking history (now blocked — see ServiceServiceDeleteGuardTests), which
// left affected customers' TotalBookings/LastVisit permanently wrong, since nothing decrements
// those denormalized counters for a row that no longer exists. GetCustomerByIdAsync already
// self-healed NoShowCount the same way on every load — extended here to TotalBookings/LastVisit
// too, so viewing the customer's admin detail page corrects the drift automatically.
public class CustomerServiceStatsSelfHealingTests
{
    private static CustomerService BuildService(GentleBook.Api.Data.GentleBookDbContext db) =>
        new(db, NullLogger<CustomerService>.Instance, new TenantContext(), TestServiceFactory.CreateEmailService(db));

    [Fact]
    public async Task GetCustomerByIdAsync_StaleTotalBookings_IsRecomputedFromSurvivingBookings()
    {
        using var db = TestDbContextFactory.Create();
        var tenant = new Tenant { Name = "Salon Stats", Slug = "salon-stats-" + Guid.NewGuid(), IsActive = true };
        db.Tenants.Add(tenant);
        var category = new ServiceCategory { TenantId = tenant.Id, Name = "Haarschnitt" };
        db.ServiceCategories.Add(category);
        var service = new Service { TenantId = tenant.Id, CategoryId = category.Id, Name = "Waschen, Schneiden, Föhnen", DurationMinutes = 30, Price = 40m, IsActive = true };
        db.Services.Add(service);
        var customer = new Customer
        {
            TenantId = tenant.Id,
            FirstName = "TEST Claude-GoLive-Check",
            LastName = "Kunde",
            Email = "golive@example.test",
            // Inflated by 3 past bookings that have since been hard-deleted elsewhere (e.g. a
            // deleted service took them with it) — only one real booking actually survives.
            TotalBookings = 4,
            LastVisit = DateTime.UtcNow.AddDays(-90),
        };
        db.Customers.Add(customer);
        var survivingBooking = new Booking
        {
            TenantId = tenant.Id,
            CustomerId = customer.Id,
            ServiceId = service.Id,
            BookingDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)),
            StartTime = new TimeOnly(11, 0),
            EndTime = new TimeOnly(11, 30),
            Status = BookingStatus.Completed,
        };
        db.Bookings.Add(survivingBooking);
        db.SaveChanges();

        var customerService = BuildService(db);
        var result = await customerService.GetCustomerByIdAsync(customer.Id, null, isAdmin: true);

        Assert.NotNull(result);
        Assert.Equal(1, result!.TotalBookings);
        Assert.Equal(
            survivingBooking.BookingDate.ToDateTime(survivingBooking.StartTime),
            result.LastVisit);

        var reloaded = await db.Customers.FindAsync(customer.Id);
        Assert.Equal(1, reloaded!.TotalBookings);
    }

    [Fact]
    public async Task GetCustomerByIdAsync_NoSurvivingBookings_ResetsCountersToZeroAndNullLastVisit()
    {
        using var db = TestDbContextFactory.Create();
        var tenant = new Tenant { Name = "Salon Stats2", Slug = "salon-stats-2-" + Guid.NewGuid(), IsActive = true };
        db.Tenants.Add(tenant);
        var customer = new Customer
        {
            TenantId = tenant.Id,
            FirstName = "TEST",
            LastName = "Orphaned",
            Email = "orphaned@example.test",
            TotalBookings = 3,
            LastVisit = DateTime.UtcNow.AddDays(-30),
        };
        db.Customers.Add(customer);
        db.SaveChanges();

        var customerService = BuildService(db);
        var result = await customerService.GetCustomerByIdAsync(customer.Id, null, isAdmin: true);

        Assert.NotNull(result);
        Assert.Equal(0, result!.TotalBookings);
        Assert.Null(result.LastVisit);
    }
}
