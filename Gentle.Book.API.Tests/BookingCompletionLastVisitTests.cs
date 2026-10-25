using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gentle.Book.API.Tests;

// Found during live pre-launch testing: Customer.LastVisit showed today's date the moment a
// booking was *created*, even for a future appointment that hadn't happened yet. The fix moves
// the update to the moment a booking is actually marked Completed — this covers that side for
// Agency tenants (the only plan BookingCompletionService's automatic job runs for).
public class BookingCompletionLastVisitTests
{
    private static BookingCompletionService Build(GentleBook.Api.Data.GentleBookDbContext db) =>
        new(db, new LoyaltyService(db, NullLogger<LoyaltyService>.Instance), NullLogger<BookingCompletionService>.Instance);

    private static (Tenant tenant, Customer customer, Booking booking) SeedOverdueConfirmedBooking(
        GentleBook.Api.Data.GentleBookDbContext db, DateTime? existingLastVisit = null)
    {
        var (tenant, _) = AgencyTenantFactory.Seed(db, SubscriptionPlan.Agency);
        db.TenantSettings.Add(new TenantSettings { TenantId = tenant.Id, CompanyName = tenant.Name, TimeZone = "Europe/Berlin" });
        var category = new ServiceCategory { TenantId = tenant.Id, Name = "Haarschnitt" };
        db.ServiceCategories.Add(category);
        var service = new Service { TenantId = tenant.Id, CategoryId = category.Id, Name = "Waschen, Schneiden, Föhnen", DurationMinutes = 30, Price = 40m, IsActive = true };
        db.Services.Add(service);
        var customer = new Customer { TenantId = tenant.Id, FirstName = "Kim", LastName = "Kunde", LastVisit = existingLastVisit };
        db.Customers.Add(customer);

        var overdueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2));
        var booking = new Booking
        {
            TenantId = tenant.Id,
            CustomerId = customer.Id,
            ServiceId = service.Id,
            BookingDate = overdueDate,
            StartTime = new TimeOnly(10, 0),
            EndTime = new TimeOnly(10, 30),
            Status = BookingStatus.Confirmed,
        };
        db.Bookings.Add(booking);
        db.SaveChanges();
        return (tenant, customer, booking);
    }

    [Fact]
    public async Task AutoCompleteBookingsAsync_MarksCompleted_SetsCustomerLastVisitToBookingDate()
    {
        using var db = TestDbContextFactory.Create();
        var (_, customer, booking) = SeedOverdueConfirmedBooking(db);
        var service = Build(db);

        await service.AutoCompleteBookingsAsync();

        var reloadedBooking = await db.Bookings.AsNoTracking().FirstAsync(b => b.Id == booking.Id);
        Assert.Equal(BookingStatus.Completed, reloadedBooking.Status);

        var reloadedCustomer = await db.Customers.AsNoTracking().FirstAsync(c => c.Id == customer.Id);
        Assert.NotNull(reloadedCustomer.LastVisit);
        Assert.Equal(booking.BookingDate.ToDateTime(booking.StartTime), reloadedCustomer.LastVisit!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task AutoCompleteBookingsAsync_ExistingLastVisitIsMoreRecent_DoesNotMoveItBackwards()
    {
        using var db = TestDbContextFactory.Create();
        var alreadyNewer = DateTime.UtcNow.AddDays(1); // customer already has a more recent (future, already-happened-by-then) visit on file
        var (_, customer, _) = SeedOverdueConfirmedBooking(db, existingLastVisit: alreadyNewer);
        var service = Build(db);

        await service.AutoCompleteBookingsAsync();

        var reloadedCustomer = await db.Customers.AsNoTracking().FirstAsync(c => c.Id == customer.Id);
        Assert.Equal(alreadyNewer, reloadedCustomer.LastVisit!.Value, TimeSpan.FromSeconds(1));
    }
}
