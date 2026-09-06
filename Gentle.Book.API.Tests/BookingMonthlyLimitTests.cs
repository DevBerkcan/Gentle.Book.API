using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.DTOs;
using GentleBook.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gentle.Book.API.Tests;

// Covers the monthly-booking-count plan limit (Trial=100, Starter=200, Professional/Agency
// unlimited) on both entry points — had zero test coverage before this pass. Only the
// rejection path is tested directly: both CreateBookingAsync and CreateManualBookingAsync open
// a SERIALIZABLE transaction immediately after this check (for slot-availability locking), and
// EF Core's InMemory provider doesn't support relational transactions — so a full successful
// booking can't be driven through these methods in a unit test. The limit check itself runs
// entirely before that transaction, so it's fully and reliably testable in isolation.
public class BookingMonthlyLimitTests
{
    private static (Tenant tenant, Service service) SeedTenantAtBookingLimit(
        GentleBook.Api.Data.GentleBookDbContext db, SubscriptionPlan plan, int existingBookingsThisMonth)
    {
        var (tenant, _) = AgencyTenantFactory.Seed(db, plan);
        var category = new ServiceCategory { TenantId = tenant.Id, Name = "Haarschnitt" };
        db.ServiceCategories.Add(category);
        var service = new Service { TenantId = tenant.Id, CategoryId = category.Id, Name = "Waschen, Schneiden, Föhnen", DurationMinutes = 30, Price = 40m, IsActive = true };
        db.Services.Add(service);

        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < existingBookingsThisMonth; i++)
        {
            db.Bookings.Add(new Booking
            {
                TenantId = tenant.Id,
                CustomerId = Guid.NewGuid(),
                ServiceId = service.Id,
                BookingDate = DateOnly.FromDateTime(monthStart),
                StartTime = new TimeOnly(10, 0),
                EndTime = new TimeOnly(10, 30),
                Status = BookingStatus.Confirmed,
                CreatedAt = monthStart.AddDays(1),
            });
        }
        db.SaveChanges();
        return (tenant, service);
    }

    private static CreateBookingDto PublicBookingDto(Guid serviceId) => new(
        serviceId, DateTime.UtcNow.AddDays(7).ToString("yyyy-MM-dd"), "11:00",
        new CustomerInfoDto("Kim", "Kunde", "kim@example.test", null), null, null);

    [Fact]
    public async Task CreateBookingAsync_StarterAtMonthlyLimit_ThrowsBookingLimitMessage()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, service) = SeedTenantAtBookingLimit(db, SubscriptionPlan.Starter, existingBookingsThisMonth: 200); // Starter MaxBookingsPerMonth = 200
        var bookingService = new BookingService(db, NullLogger<BookingService>.Instance, TestServiceFactory.CreateEmailService(db), new FakeBackgroundJobClient(), TestServiceFactory.CreateVoucherService(db));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => bookingService.CreateBookingAsync(PublicBookingDto(service.Id)));

        Assert.Contains("Buchungskontingent", ex.Message);
    }

    [Fact]
    public async Task CreateBookingAsync_TrialAtMonthlyLimit_ThrowsBookingLimitMessage()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, service) = SeedTenantAtBookingLimit(db, SubscriptionPlan.Trial, existingBookingsThisMonth: 100); // Trial MaxBookingsPerMonth = 100
        var bookingService = new BookingService(db, NullLogger<BookingService>.Instance, TestServiceFactory.CreateEmailService(db), new FakeBackgroundJobClient(), TestServiceFactory.CreateVoucherService(db));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => bookingService.CreateBookingAsync(PublicBookingDto(service.Id)));

        Assert.Contains("Buchungskontingent", ex.Message);
    }

    [Fact]
    public async Task CreateBookingAsync_ProfessionalPastStarterLimit_NeverThrowsBookingLimitMessage()
    {
        // 200 bookings would block a Starter tenant — Professional is unlimited, so the limit
        // check must not even trigger (any exception thrown past this point belongs to the
        // slot/transaction logic, not the plan-limit gate this test targets).
        using var db = TestDbContextFactory.Create();
        var (tenant, service) = SeedTenantAtBookingLimit(db, SubscriptionPlan.Professional, existingBookingsThisMonth: 200);
        var bookingService = new BookingService(db, NullLogger<BookingService>.Instance, TestServiceFactory.CreateEmailService(db), new FakeBackgroundJobClient(), TestServiceFactory.CreateVoucherService(db));

        var ex = await Record.ExceptionAsync(() => bookingService.CreateBookingAsync(PublicBookingDto(service.Id)));

        if (ex is InvalidOperationException ioe)
            Assert.DoesNotContain("Buchungskontingent", ioe.Message);
    }

    private static CreateManualBookingDto ManualBookingDto(Guid serviceId) => new(
        serviceId, DateTime.UtcNow.AddDays(7).ToString("yyyy-MM-dd"), "11:00",
        "Kim", "Kunde", "kim@example.test", null, null, null, null);

    [Fact]
    public async Task CreateManualBookingAsync_StarterAtMonthlyLimit_ThrowsBookingLimitMessage()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, service) = SeedTenantAtBookingLimit(db, SubscriptionPlan.Starter, existingBookingsThisMonth: 200);
        var tenantContext = new TenantContext();
        tenantContext.Set(tenant.Id, "test-tenant", role: "TenantAdmin");
        var manualBookingService = new ManualBookingService(
            db, NullLogger<ManualBookingService>.Instance, TestServiceFactory.CreateEmailService(db), tenantContext, TestServiceFactory.CreateVoucherService(db));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => manualBookingService.CreateManualBookingAsync(ManualBookingDto(service.Id)));

        Assert.Contains("Buchungen pro Monat", ex.Message);
    }
}
