using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.DTOs;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gentle.Book.API.Tests;

// Found during live pre-launch testing: submitting a booking took 10+ seconds and cancelling
// 15-20, because each waited on two full synchronous SMTP round trips in series (customer email
// + internal admin notification) inside the HTTP request. Both are now enqueued as Hangfire
// background jobs instead — this confirms CancelBookingAsync actually enqueues rather than
// awaiting EmailService directly (CreateBookingAsync itself can't be driven end-to-end in a unit
// test — see BookingMonthlyLimitTests for why — but it got the identical one-line change).
public class BookingServiceEmailBackgroundingTests
{
    [Fact]
    public async Task CancelBookingAsync_NotifyCustomer_EnqueuesEmailJobInsteadOfSendingInline()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = AgencyTenantFactory.Seed(db, SubscriptionPlan.Starter);
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
            BookingDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)),
            StartTime = new TimeOnly(11, 0),
            EndTime = new TimeOnly(11, 30),
            Status = BookingStatus.Confirmed,
        };
        db.Bookings.Add(booking);
        db.SaveChanges();

        var jobs = new FakeBackgroundJobClient();
        var bookingService = new BookingService(
            db, NullLogger<BookingService>.Instance, TestServiceFactory.CreateEmailService(db),
            jobs, TestServiceFactory.CreateVoucherService(db));

        var result = await bookingService.CancelBookingAsync(booking.Id, new CancelBookingDto(null, true));

        Assert.True(result.Success);
        Assert.Contains(jobs.CreatedJobs, j => j.Method.Name == nameof(EmailService.SendCancellationConfirmationByIdAsync));
        // No EmailLog row yet — proof the send didn't actually happen inline during this call.
        Assert.Empty(db.EmailLogs);
    }
}
