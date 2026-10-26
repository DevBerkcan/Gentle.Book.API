using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.DTOs;
using GentleBook.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gentle.Book.API.Tests;

// Found during live pre-launch voucher audit: cancelling, no-showing or deleting a booking that
// redeemed a voucher never restored the voucher's balance/session — the customer permanently lost
// it even though the appointment never happened. Covers VoucherService.RefundVoucherForBookingAsync
// directly and through all three call sites (BookingService.CancelBookingAsync,
// AdminService.UpdateBookingStatusAsync, AdminService.DeleteBookingAsync).
public class VoucherRefundTests
{
    private static (Tenant tenant, Service service, Customer customer) SeedBase(GentleBookDbContext db)
    {
        var (tenant, _) = AgencyTenantFactory.Seed(db, SubscriptionPlan.Agency);
        var category = new ServiceCategory { TenantId = tenant.Id, Name = "Haarschnitt" };
        db.ServiceCategories.Add(category);
        var service = new Service { TenantId = tenant.Id, CategoryId = category.Id, Name = "Waschen, Schneiden, Föhnen", DurationMinutes = 30, Price = 40m, IsActive = true };
        db.Services.Add(service);
        var customer = new Customer { TenantId = tenant.Id, FirstName = "Kim", LastName = "Kunde", Email = "kim@example.test" };
        db.Customers.Add(customer);
        db.SaveChanges();
        return (tenant, service, customer);
    }

    private static Booking SeedConfirmedBookingWithVoucher(
        GentleBookDbContext db, Tenant tenant, Service service, Customer customer, string voucherCode, decimal discountAmount)
    {
        var booking = new Booking
        {
            TenantId = tenant.Id,
            CustomerId = customer.Id,
            ServiceId = service.Id,
            BookingDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)),
            StartTime = new TimeOnly(11, 0),
            EndTime = new TimeOnly(11, 30),
            Status = BookingStatus.Confirmed,
            RedeemedVoucherCode = voucherCode,
            VoucherDiscountAmount = discountAmount,
        };
        db.Bookings.Add(booking);
        db.SaveChanges();
        return booking;
    }

    [Fact]
    public async Task RefundVoucherForBookingAsync_MonetaryValue_RestoresBalanceAndReactivatesIfFullySpent()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, service, customer) = SeedBase(db);
        var voucherService = TestServiceFactory.CreateVoucherService(db);
        var voucher = await voucherService.IssueAsync(tenant.Id, VoucherType.MonetaryValue, customer.Id, amount: 40m, null, null, null, null, Guid.NewGuid());
        // Fully redeem it against a 40€ booking, exactly matching SeedBase's service price.
        var (_, _) = await voucherService.RedeemAsync(tenant.Id, voucher.Code, service.Price);
        var reloadedAfterRedeem = await db.Vouchers.AsNoTracking().FirstAsync(v => v.Id == voucher.Id);
        Assert.Equal(0m, reloadedAfterRedeem.RemainingAmount);
        Assert.Equal(VoucherStatus.Redeemed, reloadedAfterRedeem.Status);

        var booking = SeedConfirmedBookingWithVoucher(db, tenant, service, customer, voucher.Code, service.Price);

        await voucherService.RefundVoucherForBookingAsync(booking);
        await db.SaveChangesAsync();

        var reloadedVoucher = await db.Vouchers.AsNoTracking().FirstAsync(v => v.Id == voucher.Id);
        Assert.Equal(40m, reloadedVoucher.RemainingAmount);
        Assert.Equal(VoucherStatus.Active, reloadedVoucher.Status);
        Assert.True(booking.VoucherRefunded);
    }

    [Fact]
    public async Task RefundVoucherForBookingAsync_SessionPackage_RestoresOneSession()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, service, customer) = SeedBase(db);
        var voucherService = TestServiceFactory.CreateVoucherService(db);
        var voucher = await voucherService.IssueAsync(tenant.Id, VoucherType.SessionPackage, customer.Id, null, sessions: 10, null, null, null, Guid.NewGuid());
        await voucherService.RedeemAsync(tenant.Id, voucher.Code, service.Price);
        var afterRedeem = await db.Vouchers.AsNoTracking().FirstAsync(v => v.Id == voucher.Id);
        Assert.Equal(9, afterRedeem.RemainingSessions);

        var booking = SeedConfirmedBookingWithVoucher(db, tenant, service, customer, voucher.Code, service.Price);

        await voucherService.RefundVoucherForBookingAsync(booking);
        await db.SaveChangesAsync();

        var reloaded = await db.Vouchers.AsNoTracking().FirstAsync(v => v.Id == voucher.Id);
        Assert.Equal(10, reloaded.RemainingSessions);
    }

    [Fact]
    public async Task RefundVoucherForBookingAsync_CalledTwice_DoesNotDoubleRefund()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, service, customer) = SeedBase(db);
        var voucherService = TestServiceFactory.CreateVoucherService(db);
        var voucher = await voucherService.IssueAsync(tenant.Id, VoucherType.MonetaryValue, customer.Id, amount: 40m, null, null, null, null, Guid.NewGuid());
        await voucherService.RedeemAsync(tenant.Id, voucher.Code, 20m); // partial redemption, 20€ left

        var booking = SeedConfirmedBookingWithVoucher(db, tenant, service, customer, voucher.Code, 20m);

        await voucherService.RefundVoucherForBookingAsync(booking);
        await voucherService.RefundVoucherForBookingAsync(booking); // e.g. Cancel then Delete on the same booking
        await db.SaveChangesAsync();

        var reloaded = await db.Vouchers.AsNoTracking().FirstAsync(v => v.Id == voucher.Id);
        Assert.Equal(40m, reloaded.RemainingAmount); // not 60€ — capped, refunded only once
    }

    [Fact]
    public async Task RefundVoucherForBookingAsync_VoucherSeparatelyCancelled_DoesNotResurrectIt()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, service, customer) = SeedBase(db);
        var voucherService = TestServiceFactory.CreateVoucherService(db);
        var voucher = await voucherService.IssueAsync(tenant.Id, VoucherType.MonetaryValue, customer.Id, amount: 40m, null, null, null, null, Guid.NewGuid());
        await voucherService.RedeemAsync(tenant.Id, voucher.Code, 20m);
        await voucherService.CancelAsync(tenant.Id, voucher.Id); // admin voids the voucher entirely

        var booking = SeedConfirmedBookingWithVoucher(db, tenant, service, customer, voucher.Code, 20m);

        await voucherService.RefundVoucherForBookingAsync(booking);
        await db.SaveChangesAsync();

        var reloaded = await db.Vouchers.AsNoTracking().FirstAsync(v => v.Id == voucher.Id);
        Assert.Equal(VoucherStatus.Cancelled, reloaded.Status);
        Assert.Equal(20m, reloaded.RemainingAmount); // untouched, not resurrected
    }

    [Fact]
    public async Task AdminService_UpdateBookingStatusAsync_ToCancelled_RefundsVoucher()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, service, customer) = SeedBase(db);
        var voucherService = TestServiceFactory.CreateVoucherService(db);
        var voucher = await voucherService.IssueAsync(tenant.Id, VoucherType.MonetaryValue, customer.Id, amount: 40m, null, null, null, null, Guid.NewGuid());
        await voucherService.RedeemAsync(tenant.Id, voucher.Code, 40m);
        var booking = SeedConfirmedBookingWithVoucher(db, tenant, service, customer, voucher.Code, 40m);

        var adminService = new AdminService(db, NullLogger<AdminService>.Instance, TestServiceFactory.CreateEmailService(db),
            new LoyaltyService(db, NullLogger<LoyaltyService>.Instance), voucherService);

        await adminService.UpdateBookingStatusAsync(booking.Id, new UpdateBookingStatusDto("Cancelled", null));

        var reloaded = await db.Vouchers.AsNoTracking().FirstAsync(v => v.Id == voucher.Id);
        Assert.Equal(40m, reloaded.RemainingAmount);
        var reloadedBooking = await db.Bookings.AsNoTracking().FirstAsync(b => b.Id == booking.Id);
        Assert.True(reloadedBooking.VoucherRefunded);
    }

    [Fact]
    public async Task AdminService_UpdateBookingStatusAsync_ToNoShow_DoesNotRefundVoucher()
    {
        // Deliberate policy (see audit): a no-show held the slot without cancelling in time, so
        // the voucher/session stays spent — unlike an actual cancellation.
        using var db = TestDbContextFactory.Create();
        var (tenant, service, customer) = SeedBase(db);
        var voucherService = TestServiceFactory.CreateVoucherService(db);
        var voucher = await voucherService.IssueAsync(tenant.Id, VoucherType.MonetaryValue, customer.Id, amount: 40m, null, null, null, null, Guid.NewGuid());
        await voucherService.RedeemAsync(tenant.Id, voucher.Code, 40m);
        var booking = SeedConfirmedBookingWithVoucher(db, tenant, service, customer, voucher.Code, 40m);

        var adminService = new AdminService(db, NullLogger<AdminService>.Instance, TestServiceFactory.CreateEmailService(db),
            new LoyaltyService(db, NullLogger<LoyaltyService>.Instance), voucherService);

        await adminService.UpdateBookingStatusAsync(booking.Id, new UpdateBookingStatusDto("NoShow", null));

        var reloaded = await db.Vouchers.AsNoTracking().FirstAsync(v => v.Id == voucher.Id);
        Assert.Equal(0m, reloaded.RemainingAmount);
        Assert.Equal(VoucherStatus.Redeemed, reloaded.Status);
    }

    [Fact]
    public async Task AdminService_DeleteBookingAsync_RefundsVoucher()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, service, customer) = SeedBase(db);
        var voucherService = TestServiceFactory.CreateVoucherService(db);
        var voucher = await voucherService.IssueAsync(tenant.Id, VoucherType.SessionPackage, customer.Id, null, sessions: 5, null, null, null, Guid.NewGuid());
        await voucherService.RedeemAsync(tenant.Id, voucher.Code, service.Price);
        var booking = SeedConfirmedBookingWithVoucher(db, tenant, service, customer, voucher.Code, service.Price);

        var adminService = new AdminService(db, NullLogger<AdminService>.Instance, TestServiceFactory.CreateEmailService(db),
            new LoyaltyService(db, NullLogger<LoyaltyService>.Instance), voucherService);

        await adminService.DeleteBookingAsync(booking.Id, "Testabsage");

        var reloaded = await db.Vouchers.AsNoTracking().FirstAsync(v => v.Id == voucher.Id);
        Assert.Equal(5, reloaded.RemainingSessions);
    }

    [Fact]
    public async Task BookingService_CancelBookingAsync_RefundsVoucher()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, service, customer) = SeedBase(db);
        var voucherService = TestServiceFactory.CreateVoucherService(db);
        var voucher = await voucherService.IssueAsync(tenant.Id, VoucherType.PercentageDiscount, customer.Id, null, null, percentageValue: 20m, null, null, Guid.NewGuid());
        var (_, discount) = await voucherService.RedeemAsync(tenant.Id, voucher.Code, service.Price);
        var booking = SeedConfirmedBookingWithVoucher(db, tenant, service, customer, voucher.Code, discount);

        var bookingService = new BookingService(db, NullLogger<BookingService>.Instance, TestServiceFactory.CreateEmailService(db),
            new FakeBackgroundJobClient(), voucherService);

        await bookingService.CancelBookingAsync(booking.Id, new CancelBookingDto("Kundenwunsch", false));

        var reloaded = await db.Vouchers.AsNoTracking().FirstAsync(v => v.Id == voucher.Id);
        Assert.Equal(VoucherStatus.Active, reloaded.Status);
        Assert.Equal(1, reloaded.RemainingSessions);
    }
}
