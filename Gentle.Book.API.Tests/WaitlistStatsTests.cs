using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Controllers;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Xunit;

namespace Gentle.Book.API.Tests;

// "Abgesagte Termine nachbesetzen": the studio sees how many freed slots the waitlist refilled.
public class WaitlistStatsTests
{
    [Fact]
    public async Task Stats_CountOfferedRefilledExpired_IgnoreCancelledRefills_SplitBy30Days()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = AgencyTenantFactory.Seed(db, SubscriptionPlan.Professional, name: "Studio Warteliste");
        var (other, _) = AgencyTenantFactory.Seed(db, SubscriptionPlan.Professional, name: "Studio Fremd");
        var service = new Service { TenantId = tenant.Id, Name = "Maniküre", DurationMinutes = 45, Price = 50m, IsActive = true };
        var customer = new Customer { TenantId = tenant.Id, FirstName = "W", LastName = "L" };
        db.AddRange(service, customer);
        Booking Book(BookingStatus s) { var b = new Booking { TenantId = tenant.Id, CustomerId = customer.Id, ServiceId = service.Id, Status = s, BookingDate = new DateOnly(2030, 1, 1) }; db.Bookings.Add(b); return b; }

        var kept = Book(BookingStatus.Confirmed);
        var cancelledAgain = Book(BookingStatus.Cancelled);
        var old = Book(BookingStatus.Completed);
        WaitlistEntry E(WaitlistStatus s, int daysAgo, Guid? bookingId = null, Guid? tenantId = null) =>
            new() { TenantId = tenantId ?? tenant.Id, Status = s, NotifiedAt = s == WaitlistStatus.Pending ? null : DateTime.UtcNow.AddDays(-daysAgo), BookingId = bookingId, FirstName = "x", LastName = "y", Email = "x@y.de" };
        db.WaitlistEntries.AddRange(
            E(WaitlistStatus.Booked, 2, kept.Id),
            E(WaitlistStatus.Booked, 3, cancelledAgain.Id),
            E(WaitlistStatus.Expired, 5),
            E(WaitlistStatus.Notified, 1),
            E(WaitlistStatus.Booked, 40, old.Id),
            E(WaitlistStatus.Pending, 0),
            E(WaitlistStatus.Booked, 1, kept.Id, other.Id)); // other studio — never counted
        await db.SaveChangesAsync();

        var ctx = new TenantContext();
        ctx.Set(tenant.Id, "t", isSuperAdmin: false, role: "TenantAdmin");
        var result = Assert.IsType<OkObjectResult>(await new WaitlistController(db, ctx).GetStats());
        var json = JsonSerializer.SerializeToElement(result.Value);

        var last30 = json.GetProperty("last30Days");
        Assert.Equal(4, last30.GetProperty("offered").GetInt32());
        Assert.Equal(1, last30.GetProperty("refilled").GetInt32());
        Assert.Equal(1, last30.GetProperty("expired").GetInt32());
        Assert.Equal(50m, last30.GetProperty("refilledRevenue").GetDecimal());
        var total = json.GetProperty("total");
        Assert.Equal(5, total.GetProperty("offered").GetInt32());
        Assert.Equal(2, total.GetProperty("refilled").GetInt32());
        Assert.Equal(100m, total.GetProperty("refilledRevenue").GetDecimal());
        Assert.Equal(1, json.GetProperty("waitingNow").GetInt32());

        var employeeCtx = new TenantContext();
        employeeCtx.Set(tenant.Id, "t", isSuperAdmin: false, role: "Employee");
        Assert.IsType<ForbidResult>(await new WaitlistController(db, employeeCtx).GetStats());
    }
}
