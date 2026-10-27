using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.DTOs;
using GentleBook.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gentle.Book.API.Tests;

// TenantSettings has no global query filter at all (unlike Booking/Customer/EmployeeSchedule/...
// — see SkinbloomDbContext.OnModelCreating). Several places queried it with no explicit TenantId
// filter at all, which silently returned whichever tenant's settings row the database happened
// to return first — in production, every tenant's cancellation-notice window, max-advance-
// booking-days and dashboard timezone were effectively being read from ONE tenant's settings,
// not each tenant's own. Found while investigating a separate, unrelated "wrong tracking numbers"
// report. These tests seed two tenants with deliberately different settings and confirm each
// tenant's own value is actually the one used.
public class TenantSettingsCrossTenantLeakTests
{
    private static BookingService BuildBookingService(GentleBook.Api.Data.GentleBookDbContext db) =>
        new(db, NullLogger<BookingService>.Instance, TestServiceFactory.CreateEmailService(db),
            new FakeBackgroundJobClient(), TestServiceFactory.CreateVoucherService(db));

    [Fact]
    public async Task CancelBookingAsync_UsesOwnTenantsCancellationNotice_NotAnotherTenants()
    {
        using var db = TestDbContextFactory.Create();

        var strictTenant = new Tenant { Name = "Strict Salon", Slug = "strict-" + Guid.NewGuid(), IsActive = true };
        db.Tenants.Add(strictTenant);
        db.TenantSettings.Add(new TenantSettings { TenantId = strictTenant.Id, CancellationHoursNotice = 48 });

        var lenientTenant = new Tenant { Name = "Lenient Salon", Slug = "lenient-" + Guid.NewGuid(), IsActive = true };
        db.Tenants.Add(lenientTenant);
        db.TenantSettings.Add(new TenantSettings { TenantId = lenientTenant.Id, CancellationHoursNotice = 0 });

        var category = new ServiceCategory { TenantId = lenientTenant.Id, Name = "Haarschnitt" };
        db.ServiceCategories.Add(category);
        var service = new Service { TenantId = lenientTenant.Id, CategoryId = category.Id, Name = "Waschen, Schneiden, Föhnen", DurationMinutes = 30, Price = 40m, IsActive = true };
        db.Services.Add(service);
        var customer = new Customer { TenantId = lenientTenant.Id, FirstName = "Kim", LastName = "Kunde", Email = "kim@example.test" };
        db.Customers.Add(customer);
        // Only 2 hours from now — would be rejected under the strict tenant's 48h notice, but
        // this booking belongs to the LENIENT tenant (0h notice), so it must succeed.
        var bookingDateTime = DateTime.UtcNow.AddHours(2);
        var booking = new Booking
        {
            TenantId = lenientTenant.Id,
            CustomerId = customer.Id,
            ServiceId = service.Id,
            BookingDate = DateOnly.FromDateTime(bookingDateTime),
            StartTime = TimeOnly.FromDateTime(bookingDateTime),
            EndTime = TimeOnly.FromDateTime(bookingDateTime.AddMinutes(30)),
            Status = BookingStatus.Confirmed,
        };
        db.Bookings.Add(booking);
        db.SaveChanges();

        var bookingService = BuildBookingService(db);

        var result = await bookingService.CancelBookingAsync(booking.Id, new CancelBookingDto(null, false));

        Assert.True(result.Success);
    }

    [Fact]
    public async Task CancelBookingAsync_StrictTenantsOwnNotice_StillEnforced()
    {
        // Mirror of the test above with the tenants' roles reversed, so a fix that merely
        // "always picks the lenient one" (e.g. by seeding/insertion order) can't pass both.
        using var db = TestDbContextFactory.Create();

        var lenientTenant = new Tenant { Name = "Lenient Salon2", Slug = "lenient2-" + Guid.NewGuid(), IsActive = true };
        db.Tenants.Add(lenientTenant);
        db.TenantSettings.Add(new TenantSettings { TenantId = lenientTenant.Id, CancellationHoursNotice = 0 });

        var strictTenant = new Tenant { Name = "Strict Salon2", Slug = "strict2-" + Guid.NewGuid(), IsActive = true };
        db.Tenants.Add(strictTenant);
        db.TenantSettings.Add(new TenantSettings { TenantId = strictTenant.Id, CancellationHoursNotice = 48 });

        var category = new ServiceCategory { TenantId = strictTenant.Id, Name = "Haarschnitt" };
        db.ServiceCategories.Add(category);
        var service = new Service { TenantId = strictTenant.Id, CategoryId = category.Id, Name = "Waschen, Schneiden, Föhnen", DurationMinutes = 30, Price = 40m, IsActive = true };
        db.Services.Add(service);
        var customer = new Customer { TenantId = strictTenant.Id, FirstName = "Kim", LastName = "Kunde", Email = "kim@example.test" };
        db.Customers.Add(customer);
        var bookingDateTime = DateTime.UtcNow.AddHours(2);
        var booking = new Booking
        {
            TenantId = strictTenant.Id,
            CustomerId = customer.Id,
            ServiceId = service.Id,
            BookingDate = DateOnly.FromDateTime(bookingDateTime),
            StartTime = TimeOnly.FromDateTime(bookingDateTime),
            EndTime = TimeOnly.FromDateTime(bookingDateTime.AddMinutes(30)),
            Status = BookingStatus.Confirmed,
        };
        db.Bookings.Add(booking);
        db.SaveChanges();

        var bookingService = BuildBookingService(db);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => bookingService.CancelBookingAsync(booking.Id, new CancelBookingDto(null, false)));
        Assert.Contains("48 Stunden", ex.Message);
    }
}
