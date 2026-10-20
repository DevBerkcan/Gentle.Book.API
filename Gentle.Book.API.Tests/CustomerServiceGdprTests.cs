using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gentle.Book.API.Tests;

// Covers the GDPR gap from LEGAL_AUDIT_2026-09-06.md: "Kundenlöschung löscht statt zu
// anonymisieren" (customer deletion hard-deleted instead of anonymizing) and "kein
// Kunden-Datenexport (Art. 15)" (no per-customer data export). DeleteCustomerAsync previously
// hard-deleted the Customer row AND every Booking, destroying the salon's own legitimate
// revenue/statistics records along with the customer's PII; it must now only clear the
// identifying fields. ExportCustomerDataAsync is new — a full copy, not the 10-most-recent
// subset GetCustomerByIdAsync returns for the admin-UI detail view.
public class CustomerServiceGdprTests
{
    private static CustomerService BuildService(GentleBook.Api.Data.GentleBookDbContext db) =>
        new(db, NullLogger<CustomerService>.Instance, new TenantContext(), TestServiceFactory.CreateEmailService(db));

    private static (Tenant tenant, Customer customer, List<Booking> bookings) SeedCustomerWithBookings(
        GentleBook.Api.Data.GentleBookDbContext db, int bookingCount)
    {
        var tenant = new Tenant { Name = "Salon Auskunft", Slug = "salon-auskunft-" + Guid.NewGuid(), IsActive = true };
        db.Tenants.Add(tenant);
        var category = new ServiceCategory { TenantId = tenant.Id, Name = "Haarschnitt" };
        db.ServiceCategories.Add(category);
        var service = new Service { TenantId = tenant.Id, CategoryId = category.Id, Name = "Waschen, Schneiden, Föhnen", DurationMinutes = 30, Price = 40m, IsActive = true };
        db.Services.Add(service);
        var customer = new Customer
        {
            TenantId = tenant.Id,
            FirstName = "Lena",
            LastName = "Kunde",
            Email = "lena@example.test",
            Phone = "+491234567",
            Notes = "Allergisch gegen Ammoniak.",
        };
        db.Customers.Add(customer);

        var bookings = new List<Booking>();
        for (var i = 0; i < bookingCount; i++)
        {
            var booking = new Booking
            {
                TenantId = tenant.Id,
                CustomerId = customer.Id,
                ServiceId = service.Id,
                BookingDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(i)),
                StartTime = new TimeOnly(11, 0),
                EndTime = new TimeOnly(11, 30),
                Status = BookingStatus.Confirmed,
                CustomerNotes = $"Notiz {i}",
                CreatedAt = DateTime.UtcNow,
            };
            db.Bookings.Add(booking);
            bookings.Add(booking);
        }
        db.SaveChanges();
        return (tenant, customer, bookings);
    }

    [Fact]
    public async Task DeleteCustomerAsync_AnonymizesPii_KeepsBookingsForRevenueStats()
    {
        using var db = TestDbContextFactory.Create();
        var (_, customer, bookings) = SeedCustomerWithBookings(db, 2);
        var service = BuildService(db);

        await service.DeleteCustomerAsync(customer.Id, employeeId: null, isAdmin: true);

        var reloaded = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == customer.Id);
        Assert.NotNull(reloaded); // row is NOT removed
        Assert.Equal("Gelöschter", reloaded!.FirstName);
        Assert.Equal("Kunde", reloaded.LastName);
        Assert.Null(reloaded.Email);
        Assert.Null(reloaded.Phone);
        Assert.Null(reloaded.Notes);

        var remainingBookings = await db.Bookings.AsNoTracking().Where(b => b.CustomerId == customer.Id).ToListAsync();
        Assert.Equal(bookings.Count, remainingBookings.Count); // bookings survive for revenue/stats
        Assert.All(remainingBookings, b => Assert.Null(b.CustomerNotes));
    }

    [Fact]
    public async Task ExportCustomerDataAsync_ReturnsFullProfileAndEveryBooking()
    {
        using var db = TestDbContextFactory.Create();
        const int bookingCount = 15; // more than GetCustomerByIdAsync's 10-most-recent cap
        var (_, customer, _) = SeedCustomerWithBookings(db, bookingCount);
        var service = BuildService(db);

        var export = await service.ExportCustomerDataAsync(customer.Id, employeeId: null, isAdmin: true);

        Assert.NotNull(export);
        var profile = GetProperty(export!, "Profile");
        Assert.Equal("Lena", GetProperty(profile, "FirstName"));
        Assert.Equal("lena@example.test", GetProperty(profile, "Email"));

        var exportedBookings = (System.Collections.IEnumerable)GetProperty(export!, "Bookings");
        Assert.Equal(bookingCount, exportedBookings.Cast<object>().Count());
    }

    [Fact]
    public async Task ExportCustomerDataAsync_UnknownCustomer_ReturnsNull()
    {
        using var db = TestDbContextFactory.Create();
        var service = BuildService(db);

        var export = await service.ExportCustomerDataAsync(Guid.NewGuid(), employeeId: null, isAdmin: true);

        Assert.Null(export);
    }

    private static object GetProperty(object obj, string name) =>
        obj.GetType().GetProperty(name)!.GetValue(obj)!;
}
