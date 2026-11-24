using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.DTOs;
using GentleBook.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gentle.Book.API.Tests;

// Found during the treatment-package work: a manual booking with a chosen employee only looked
// for the client among THAT employee's customers, so booking an existing client (assigned to no
// one or to a colleague) silently created a second Kundenkartei — splitting packages, stamp card
// and referral rewards. E-mail/phone now match studio-wide; name-only matching stays scoped.
public class ManualBookingCustomerMatchingTests
{
    private static (Tenant tenant, Guid empA, Guid empB, ManualBookingService svc, GentleBookDbContext db) Build()
    {
        var db = TestDbContextFactory.Create();
        var (tenant, _) = AgencyTenantFactory.Seed(db, SubscriptionPlan.Professional, name: "Studio Matching");
        var ctx = new TenantContext();
        ctx.Set(tenant.Id, "t", role: "TenantAdmin");
        var svc = new ManualBookingService(db, NullLogger<ManualBookingService>.Instance,
            TestServiceFactory.CreateEmailService(db), ctx, TestServiceFactory.CreateVoucherService(db));
        return (tenant, Guid.NewGuid(), Guid.NewGuid(), svc, db);
    }

    private static CreateManualBookingDto Dto(string first, string last, string? email, string? phone, Guid? employeeId) =>
        new(Guid.NewGuid(), "2030-01-10", "10:00", first, last, email, phone, null, employeeId, null);

    [Fact]
    public async Task ExistingClientWithoutEmployee_IsReused_WhenAdminBooksWithAnEmployee()
    {
        var (tenant, empA, _, svc, db) = Build();
        var existing = new Customer { TenantId = tenant.Id, FirstName = "Lena", LastName = "Kunde", Email = "lena@example.de", EmployeeId = null };
        db.Customers.Add(existing);
        await db.SaveChangesAsync();

        var c = await svc.FindOrCreateCustomerForManualBookingAsync(Dto("Lena", "Kunde", "LENA@example.de", null, empA), empA);

        Assert.Equal(existing.Id, c.Id);
        Assert.Equal(1, await db.Customers.CountAsync());
    }

    [Fact]
    public async Task ColleaguesClient_IsReusedByEmailAndPhone_NotDuplicated()
    {
        var (tenant, empA, empB, svc, db) = Build();
        var existing = new Customer { TenantId = tenant.Id, FirstName = "Mia", LastName = "Stamm", Email = "mia@example.de", Phone = "0151 111", EmployeeId = empA };
        db.Customers.Add(existing);
        await db.SaveChangesAsync();

        Assert.Equal(existing.Id, (await svc.FindOrCreateCustomerForManualBookingAsync(Dto("Mia", "Stamm", "mia@example.de", null, empB), empB)).Id);
        Assert.Equal(existing.Id, (await svc.FindOrCreateCustomerForManualBookingAsync(Dto("Mia", "Stamm", null, "0151 111", empB), empB)).Id);
        Assert.Equal(1, await db.Customers.CountAsync());
        // Still assigned to her original employee.
        Assert.Equal(empA, (await db.Customers.FindAsync(existing.Id))!.EmployeeId);
    }

    [Fact]
    public async Task NameOnlyMatch_StaysScopedToEmployee_SoNamesakesAreNotMerged()
    {
        var (tenant, empA, empB, svc, db) = Build();
        var anna = new Customer { TenantId = tenant.Id, FirstName = "Anna", LastName = "Müller", EmployeeId = empA };
        db.Customers.Add(anna);
        await db.SaveChangesAsync();

        var other = await svc.FindOrCreateCustomerForManualBookingAsync(Dto("Anna", "Müller", null, null, empB), empB);
        await db.SaveChangesAsync();

        Assert.NotEqual(anna.Id, other.Id);
        Assert.Equal(anna.Id, (await svc.FindOrCreateCustomerForManualBookingAsync(Dto("Anna", "Müller", null, null, empA), empA)).Id);
    }

    [Fact]
    public async Task OtherStudiosClient_IsNeverMatched()
    {
        var (tenant, empA, _, svc, db) = Build();
        var (otherTenant, _) = AgencyTenantFactory.Seed(db, SubscriptionPlan.Professional, name: "Studio Fremd");
        db.Customers.Add(new Customer { TenantId = otherTenant.Id, FirstName = "Lena", LastName = "Kunde", Email = "lena@example.de" });
        await db.SaveChangesAsync();

        var c = await svc.FindOrCreateCustomerForManualBookingAsync(Dto("Lena", "Kunde", "lena@example.de", null, empA), empA);
        await db.SaveChangesAsync();

        Assert.Equal(tenant.Id, c.TenantId);
        Assert.Equal(2, await db.Customers.IgnoreQueryFilters().CountAsync());
    }
}
