using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.DTOs;
using GentleBook.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gentle.Book.API.Tests;

// Found during live pre-launch testing: three old bookings looked "missing" from the admin
// bookings list even though they showed up in the audit log and in the customer's booking
// count/last-visit — they weren't missing at all, just unreachable through this search box. The
// booking number shown everywhere a booking is referenced (BK-yyyyMMdd-XXXXXXXX, from
// Booking.GenerateBookingNumber) is derived from the Id and isn't a stored column, and the
// search filter never matched the Id at all — only customer name/email/phone and service name.
public class AdminServiceBookingSearchTests
{
    private static AdminService Build(GentleBook.Api.Data.GentleBookDbContext db) =>
        new(db, NullLogger<AdminService>.Instance, TestServiceFactory.CreateEmailService(db), new LoyaltyService(db, NullLogger<LoyaltyService>.Instance), TestServiceFactory.CreateVoucherService(db), new FakeBackgroundJobClient());

    private static (Tenant tenant, Booking booking) SeedBooking(GentleBook.Api.Data.GentleBookDbContext db)
    {
        var tenant = new Tenant { Name = "Salon Search", Slug = "salon-search-" + Guid.NewGuid(), IsActive = true };
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
            Status = BookingStatus.NoShow,
        };
        db.Bookings.Add(booking);
        db.SaveChanges();
        return (tenant, booking);
    }

    [Fact]
    public async Task GetBookings_SearchByBareIdFragment_FindsTheBooking()
    {
        using var db = TestDbContextFactory.Create();
        var (_, booking) = SeedBooking(db);
        var idFragment = booking.Id.ToString()[..8]; // lowercase, as GenerateBookingNumber uppercases it
        var adminService = Build(db);

        var result = await adminService.GetBookingsAsync(new BookingFilterDto(null, null, null, null, idFragment.ToUpperInvariant()));

        Assert.Contains(result.Items, b => b.Id == booking.Id);
    }

    [Fact]
    public async Task GetBookings_SearchByFullBookingNumber_FindsTheBooking()
    {
        using var db = TestDbContextFactory.Create();
        var (_, booking) = SeedBooking(db);
        var bookingNumber = Booking.GenerateBookingNumber(booking.BookingDate, booking.Id);
        var adminService = Build(db);

        var result = await adminService.GetBookingsAsync(new BookingFilterDto(null, null, null, null, bookingNumber));

        Assert.Contains(result.Items, b => b.Id == booking.Id);
    }

    [Fact]
    public async Task GetBookings_SearchByUnrelatedTerm_DoesNotMatch()
    {
        using var db = TestDbContextFactory.Create();
        var (_, booking) = SeedBooking(db);
        var adminService = Build(db);

        var result = await adminService.GetBookingsAsync(new BookingFilterDto(null, null, null, null, "deadbeef"));

        Assert.DoesNotContain(result.Items, b => b.Id == booking.Id);
    }
}
