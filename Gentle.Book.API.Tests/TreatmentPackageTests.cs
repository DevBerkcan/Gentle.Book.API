using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Controllers;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.DTOs;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gentle.Book.API.Tests;

// Behandlungspakete + "Offene Folgetermine". Endpoints that open a SERIALIZABLE transaction
// (public link booking, manual booking, AssignToBooking) can't run on EF InMemory — those are
// covered by the real-SQL acceptance script; everything else (balance derivation, completion
// guard, dashboard reasons, plan gate, roles, tenant isolation, public link check) is here.
public class TreatmentPackageTests
{
    private sealed record Studio(Tenant Tenant, Service Service, Customer Customer);

    private static Studio SeedStudio(GentleBookDbContext db, SubscriptionPlan plan, string name)
    {
        var (tenant, _) = AgencyTenantFactory.Seed(db, plan, name: name);
        db.TenantSettings.Add(new TenantSettings { TenantId = tenant.Id, CompanyName = name, TimeZone = "Europe/Berlin" });
        var service = new Service { TenantId = tenant.Id, Name = "Laser Achseln", DurationMinutes = 30, Price = 80m, IsActive = true };
        var customer = new Customer { TenantId = tenant.Id, FirstName = "Lena", LastName = "Kunde", Email = $"lena-{Guid.NewGuid():N}@example.de", Notes = "Hauttyp II, empfindlich" };
        db.AddRange(service, customer);
        db.SaveChanges();
        return new Studio(tenant, service, customer);
    }

    private static GentleBookDbContext ScopedDb(string dbName, Guid tenantId, string role)
    {
        var ctx = new TenantContext();
        ctx.Set(tenantId, "t", isSuperAdmin: false, role: role);
        return new GentleBookDbContext(new DbContextOptionsBuilder<GentleBookDbContext>().UseInMemoryDatabase(dbName).Options, ctx);
    }

    private static TreatmentPackagesController Controller(GentleBookDbContext db, Guid tenantId, string role = "TenantAdmin", Guid? userId = null)
    {
        var ctx = new TenantContext();
        ctx.Set(tenantId, "t", isSuperAdmin: false, role: role);
        var user = role == "Employee"
            ? ClaimsPrincipalFactory.Employee(userId ?? Guid.NewGuid(), tenantId)
            : ClaimsPrincipalFactory.TenantAdmin(userId ?? Guid.NewGuid(), tenantId);
        var http = new DefaultHttpContext { User = user };
        var audit = new AuditService(db, new HttpContextAccessor { HttpContext = http }, NullLogger<AuditService>.Instance);
        return new TreatmentPackagesController(db, ctx, audit) { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    private static AdminService Admin(GentleBookDbContext db) =>
        new(db, NullLogger<AdminService>.Instance, TestServiceFactory.CreateEmailService(db),
            new LoyaltyService(db, NullLogger<LoyaltyService>.Instance), TestServiceFactory.CreateVoucherService(db),
            new FakeBackgroundJobClient(), new TenantContext());

    private static Booking AddBooking(GentleBookDbContext db, Studio s, Guid? packageId, int daysFromToday, BookingStatus status, Guid? employeeId = null)
    {
        var b = new Booking
        {
            TenantId = s.Tenant.Id, CustomerId = s.Customer.Id, ServiceId = s.Service.Id, EmployeeId = employeeId,
            BookingDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(daysFromToday)),
            StartTime = new TimeOnly(10, 0), EndTime = new TimeOnly(10, 30),
            Status = status, TreatmentPackageId = packageId,
        };
        db.Bookings.Add(b);
        db.SaveChanges();
        return b;
    }

    private static T OkValue<T>(IActionResult r) => Assert.IsType<T>(Assert.IsType<OkObjectResult>(r).Value);

    private static async Task<TreatmentPackageDto> CreatePackage(GentleBookDbContext db, Studio s, int total = 6, int previouslyUsed = 0)
        => OkValue<TreatmentPackageDto>(await Controller(db, s.Tenant.Id).Create(
            new CreateTreatmentPackageDto(s.Customer.Id, s.Service.Id, total, null, previouslyUsed)));

    private static async Task<TreatmentPackageDto> Reload(GentleBookDbContext db, Studio s, Guid packageId)
        => OkValue<List<TreatmentPackageDto>>(await Controller(db, s.Tenant.Id).GetForCustomer(s.Customer.Id)).Single(p => p.Id == packageId);

    private static async Task<List<FollowUpDto>> FollowUps(GentleBookDbContext db, Studio s)
        => OkValue<List<FollowUpDto>>(await Controller(db, s.Tenant.Id).GetFollowUps());

    // ── The acceptance scenario from the spec ────────────────────

    [Fact]
    public async Task Acceptance_SixSessions_ThreeDone_FollowUpAppearsDisappearsReappears_BalanceOnlyMovesOnCompletion()
    {
        using var db = TestDbContextFactory.Create();
        var s = SeedStudio(db, SubscriptionPlan.Professional, "Laserstudio A");
        var admin = Admin(db);

        var pkg = await CreatePackage(db, s, total: 6);
        Assert.Equal("Laser Achseln – 6 Sitzungen", pkg.Name);
        Assert.Equal(6, pkg.OpenSessions);

        // Three sessions booked (past) and completed through the normal status flow.
        for (var i = 3; i >= 1; i--)
        {
            var b = AddBooking(db, s, pkg.Id, -7 * i, BookingStatus.Confirmed);
            await admin.UpdateBookingStatusAsync(b.Id, new UpdateBookingStatusDto("Completed", null));
        }
        var afterThree = await Reload(db, s, pkg.Id);
        Assert.Equal(3, afterThree.UsedSessions);
        Assert.Equal(3, afterThree.OpenSessions);

        // No follow-up booked → appears on the dashboard.
        var list = await FollowUps(db, s);
        var entry = Assert.Single(list);
        Assert.Equal(pkg.Id, entry.PackageId);
        Assert.Equal("Lena Kunde", entry.CustomerName);
        Assert.Equal("Laser Achseln", entry.ServiceName);
        Assert.Equal(3, entry.OpenSessions);
        Assert.Equal("Folgetermin fehlt", entry.Reason);
        Assert.Contains("packageToken=", entry.BookingLinkPath);

        // Follow-up booked for this package → disappears; booking alone doesn't touch the balance.
        var followUp = AddBooking(db, s, pkg.Id, 14, BookingStatus.Confirmed);
        Assert.Empty(await FollowUps(db, s));
        Assert.Equal(3, (await Reload(db, s, pkg.Id)).OpenSessions);

        // Cancelled without replacement → reappears with the right reason; balance still 3.
        await admin.UpdateBookingStatusAsync(followUp.Id, new UpdateBookingStatusDto("Cancelled", null));
        entry = Assert.Single(await FollowUps(db, s));
        Assert.Equal("cancelled_without_replacement", entry.ReasonCode);
        Assert.Equal("Absage ohne Ersatztermin", entry.Reason);
        Assert.Equal(3, (await Reload(db, s, pkg.Id)).OpenSessions);

        // Another linked session completed → 2.
        var fourth = AddBooking(db, s, pkg.Id, -1, BookingStatus.Confirmed);
        Assert.Equal(3, (await Reload(db, s, pkg.Id)).OpenSessions); // booked, not completed yet
        await admin.UpdateBookingStatusAsync(fourth.Id, new UpdateBookingStatusDto("Completed", null));
        Assert.Equal(2, (await Reload(db, s, pkg.Id)).OpenSessions);
    }

    // ── Double debit / completion guards ─────────────────────────

    [Fact]
    public async Task CompletingTheSameBookingTwice_DebitsOnlyOnce()
    {
        using var db = TestDbContextFactory.Create();
        var s = SeedStudio(db, SubscriptionPlan.Agency, "Studio Doppel");
        var pkg = await CreatePackage(db, s, total: 6);
        var b = AddBooking(db, s, pkg.Id, -1, BookingStatus.Confirmed);
        var admin = Admin(db);

        await admin.UpdateBookingStatusAsync(b.Id, new UpdateBookingStatusDto("Completed", null));
        await admin.UpdateBookingStatusAsync(b.Id, new UpdateBookingStatusDto("Completed", "nochmal gespeichert"));
        await admin.UpdateBookingStatusAsync(b.Id, new UpdateBookingStatusDto("Completed", null));

        Assert.Equal(1, (await Reload(db, s, pkg.Id)).UsedSessions);
        Assert.Equal(5, (await Reload(db, s, pkg.Id)).OpenSessions);
    }

    [Fact]
    public async Task CompletingBeyondPackageSize_IsRejected()
    {
        using var db = TestDbContextFactory.Create();
        var s = SeedStudio(db, SubscriptionPlan.Professional, "Studio Voll");
        var pkg = await CreatePackage(db, s, total: 2, previouslyUsed: 1);
        var admin = Admin(db);
        var first = AddBooking(db, s, pkg.Id, -2, BookingStatus.Confirmed);
        await admin.UpdateBookingStatusAsync(first.Id, new UpdateBookingStatusDto("Completed", null));

        var extra = AddBooking(db, s, pkg.Id, -1, BookingStatus.Confirmed);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            admin.UpdateBookingStatusAsync(extra.Id, new UpdateBookingStatusDto("Completed", null)));
        Assert.Equal(0, (await Reload(db, s, pkg.Id)).OpenSessions);
        Assert.Equal("UsedUp", (await Reload(db, s, pkg.Id)).Status);
    }

    [Fact]
    public async Task DeletingACompletedPackageSession_IsRejected_ButSingleBookingsStillDeletable()
    {
        using var db = TestDbContextFactory.Create();
        var s = SeedStudio(db, SubscriptionPlan.Professional, "Studio Löschen");
        var pkg = await CreatePackage(db, s);
        var admin = Admin(db);
        var session = AddBooking(db, s, pkg.Id, -1, BookingStatus.Confirmed);
        await admin.UpdateBookingStatusAsync(session.Id, new UpdateBookingStatusDto("Completed", null));

        await Assert.ThrowsAsync<InvalidOperationException>(() => admin.DeleteBookingAsync(session.Id, "versehentlich"));
        Assert.Equal(1, (await Reload(db, s, pkg.Id)).UsedSessions);

        var single = AddBooking(db, s, null, -1, BookingStatus.Completed);
        await admin.DeleteBookingAsync(single.Id, "Test");
        Assert.False(await db.Bookings.AnyAsync(b => b.Id == single.Id));
    }

    // ── Existing single bookings unaffected ──────────────────────

    [Fact]
    public async Task SingleBookings_WithoutPackage_CompleteAndCancelAsBefore_AndNeverShowOnDashboard()
    {
        using var db = TestDbContextFactory.Create();
        var s = SeedStudio(db, SubscriptionPlan.Professional, "Studio Einzel");
        var admin = Admin(db);
        var a = AddBooking(db, s, null, -1, BookingStatus.Confirmed);
        var b = AddBooking(db, s, null, 3, BookingStatus.Confirmed);

        await admin.UpdateBookingStatusAsync(a.Id, new UpdateBookingStatusDto("Completed", null));
        await admin.UpdateBookingStatusAsync(b.Id, new UpdateBookingStatusDto("Cancelled", null));
        await admin.UpdateBookingStatusAsync(b.Id, new UpdateBookingStatusDto("Confirmed", null));

        Assert.Equal(BookingStatus.Completed, (await db.Bookings.FindAsync(a.Id))!.Status);
        Assert.Equal(BookingStatus.Confirmed, (await db.Bookings.FindAsync(b.Id))!.Status);
        Assert.Empty(await FollowUps(db, s));
    }

    // ── Dashboard filtering ──────────────────────────────────────

    [Fact]
    public async Task FollowUps_ExcludeUsedUpAndCancelledPackages_AndReportReasons()
    {
        using var db = TestDbContextFactory.Create();
        var s = SeedStudio(db, SubscriptionPlan.Agency, "Studio Gründe");
        var controller = Controller(db, s.Tenant.Id);

        var usedUp = await CreatePackage(db, s, total: 1);
        var done = AddBooking(db, s, usedUp.Id, -3, BookingStatus.Confirmed);
        await Admin(db).UpdateBookingStatusAsync(done.Id, new UpdateBookingStatusDto("Completed", null));

        var cancelled = await CreatePackage(db, s, total: 4);
        await controller.Update(cancelled.Id, new UpdateTreatmentPackageDto(Status: "Cancelled"));

        var neverBooked = await CreatePackage(db, s, total: 3);
        var noShow = await CreatePackage(db, s, total: 3);
        AddBooking(db, s, noShow.Id, -2, BookingStatus.NoShow);
        var notMarked = await CreatePackage(db, s, total: 3);
        AddBooking(db, s, notMarked.Id, -2, BookingStatus.Confirmed);

        var list = await FollowUps(db, s);
        Assert.Equal(3, list.Count);
        Assert.DoesNotContain(list, f => f.PackageId == usedUp.Id || f.PackageId == cancelled.Id);
        Assert.Equal("no_appointment_yet", list.Single(f => f.PackageId == neverBooked.Id).ReasonCode);
        Assert.Equal("no_show_without_replacement", list.Single(f => f.PackageId == noShow.Id).ReasonCode);
        Assert.Equal("last_not_completed", list.Single(f => f.PackageId == notMarked.Id).ReasonCode);
    }

    [Fact]
    public async Task Capacity_CannotReserveMoreAppointmentsThanOpenSessions()
    {
        using var db = TestDbContextFactory.Create();
        var s = SeedStudio(db, SubscriptionPlan.Professional, "Studio Kapazität");
        var dto = await CreatePackage(db, s, total: 2);
        var pkg = await db.TreatmentPackages.FirstAsync(p => p.Id == dto.Id);

        AddBooking(db, s, pkg.Id, 5, BookingStatus.Confirmed);
        Assert.Null(await TreatmentPackageRules.ValidateAssignmentAsync(db, pkg, s.Customer.Id, s.Service.Id, null));
        AddBooking(db, s, pkg.Id, 12, BookingStatus.Confirmed);
        Assert.NotNull(await TreatmentPackageRules.ValidateAssignmentAsync(db, pkg, s.Customer.Id, s.Service.Id, null));

        // Wrong customer / wrong service are rejected regardless of capacity.
        Assert.NotNull(await TreatmentPackageRules.ValidateAssignmentAsync(db, pkg, Guid.NewGuid(), s.Service.Id, null));
        Assert.NotNull(await TreatmentPackageRules.ValidateAssignmentAsync(db, pkg, s.Customer.Id, Guid.NewGuid(), null));
    }

    [Fact]
    public async Task Update_CannotShrinkBelowUsedPlusBooked()
    {
        using var db = TestDbContextFactory.Create();
        var s = SeedStudio(db, SubscriptionPlan.Professional, "Studio Shrink");
        var pkg = await CreatePackage(db, s, total: 6);
        var done = AddBooking(db, s, pkg.Id, -1, BookingStatus.Confirmed);
        await Admin(db).UpdateBookingStatusAsync(done.Id, new UpdateBookingStatusDto("Completed", null));
        AddBooking(db, s, pkg.Id, 3, BookingStatus.Confirmed);

        Assert.IsType<BadRequestObjectResult>(await Controller(db, s.Tenant.Id).Update(pkg.Id, new UpdateTreatmentPackageDto(TotalSessions: 1)));
        var ok = OkValue<TreatmentPackageDto>(await Controller(db, s.Tenant.Id).Update(pkg.Id, new UpdateTreatmentPackageDto(TotalSessions: 2)));
        Assert.Equal(1, ok.OpenSessions);
    }

    // ── Plan gate ────────────────────────────────────────────────

    [Fact]
    public async Task Starter_GetsNoPackageAccess_ButNotesStillVisibleInBookingView()
    {
        using var db = TestDbContextFactory.Create();
        var s = SeedStudio(db, SubscriptionPlan.Starter, "Studio Starter");
        var c = Controller(db, s.Tenant.Id);

        Assert.Equal(402, Assert.IsType<ObjectResult>(await c.GetForCustomer(s.Customer.Id)).StatusCode);
        Assert.Equal(402, Assert.IsType<ObjectResult>(await c.GetFollowUps()).StatusCode);
        Assert.Equal(402, Assert.IsType<ObjectResult>(await c.Create(new CreateTreatmentPackageDto(s.Customer.Id, s.Service.Id, 6))).StatusCode);

        var booking = AddBooking(db, s, null, 2, BookingStatus.Confirmed);
        var info = OkValue<BookingPackageInfoDto>(await c.GetForBooking(booking.Id));
        Assert.False(info.PackagesEnabled);
        Assert.Equal("Hauttyp II, empfindlich", info.CustomerInternalNotes);
    }

    // ── Roles ────────────────────────────────────────────────────

    [Fact]
    public async Task Employee_SeesPanelOnlyForOwnBookings_AndCannotManagePackages()
    {
        using var db = TestDbContextFactory.Create();
        var s = SeedStudio(db, SubscriptionPlan.Professional, "Studio Team");
        var pkg = await CreatePackage(db, s);
        var me = Guid.NewGuid();
        var mine = AddBooking(db, s, pkg.Id, 2, BookingStatus.Confirmed, employeeId: me);
        var colleagues = AddBooking(db, s, null, 3, BookingStatus.Confirmed, employeeId: Guid.NewGuid());
        var emp = Controller(db, s.Tenant.Id, "Employee", me);

        var info = OkValue<BookingPackageInfoDto>(await emp.GetForBooking(mine.Id));
        Assert.Equal(pkg.Id, info.AssignedPackage!.Id);
        Assert.Equal("Hauttyp II, empfindlich", info.CustomerInternalNotes);
        Assert.False(info.CanAssign);
        Assert.Empty(info.Candidates);

        Assert.IsType<ForbidResult>(await emp.GetForBooking(colleagues.Id));
        Assert.IsType<ForbidResult>(await emp.GetForCustomer(s.Customer.Id));
        Assert.IsType<ForbidResult>(await emp.GetFollowUps());
        Assert.IsType<ForbidResult>(await emp.Create(new CreateTreatmentPackageDto(s.Customer.Id, s.Service.Id, 3)));
        Assert.IsType<ForbidResult>(await emp.Update(pkg.Id, new UpdateTreatmentPackageDto(Status: "Cancelled")));
    }

    [Fact]
    public async Task Admin_BookingView_OffersMatchingCandidatesOnly()
    {
        using var db = TestDbContextFactory.Create();
        var s = SeedStudio(db, SubscriptionPlan.Professional, "Studio Auswahl");
        var p1 = await CreatePackage(db, s, total: 6);
        var p2 = await CreatePackage(db, s, total: 3);
        var other = new Service { TenantId = s.Tenant.Id, Name = "Laser Beine", DurationMinutes = 60, IsActive = true };
        db.Services.Add(other); db.SaveChanges();
        await Controller(db, s.Tenant.Id).Create(new CreateTreatmentPackageDto(s.Customer.Id, other.Id, 6));

        var booking = AddBooking(db, s, null, 4, BookingStatus.Confirmed);
        var info = OkValue<BookingPackageInfoDto>(await Controller(db, s.Tenant.Id).GetForBooking(booking.Id));

        Assert.True(info.CanAssign);
        Assert.Null(info.AssignedPackage);
        Assert.Equal(new[] { p1.Id, p2.Id }.OrderBy(x => x), info.Candidates.Select(c => c.Id).OrderBy(x => x));

        // A completed package session is frozen: no link/unlink offered.
        var done = AddBooking(db, s, p1.Id, -1, BookingStatus.Confirmed);
        await Admin(db).UpdateBookingStatusAsync(done.Id, new UpdateBookingStatusDto("Completed", null));
        var doneInfo = OkValue<BookingPackageInfoDto>(await Controller(db, s.Tenant.Id).GetForBooking(done.Id));
        Assert.Equal(p1.Id, doneInfo.AssignedPackage!.Id);
        Assert.False(doneInfo.CanAssign);
        Assert.Empty(doneInfo.Candidates);
    }

    // ── Tenant isolation ─────────────────────────────────────────

    [Fact]
    public async Task OtherStudio_CannotSeeOrChangeAnything()
    {
        var dbName = Guid.NewGuid().ToString();
        Studio a, b;
        TreatmentPackageDto pkgA;
        Guid bookingA;
        using (var seed = TestDbContextFactory.Create(dbName))
        {
            a = SeedStudio(seed, SubscriptionPlan.Agency, "Studio A");
            b = SeedStudio(seed, SubscriptionPlan.Agency, "Studio B");
        }
        using (var dbA = ScopedDb(dbName, a.Tenant.Id, "TenantAdmin"))
        {
            pkgA = await CreatePackage(dbA, a);
            bookingA = AddBooking(dbA, a, pkgA.Id, 3, BookingStatus.Confirmed).Id;
        }

        using var dbB = ScopedDb(dbName, b.Tenant.Id, "TenantAdmin");
        var cb = Controller(dbB, b.Tenant.Id);

        Assert.IsType<NotFoundObjectResult>(await cb.GetForCustomer(a.Customer.Id));
        Assert.IsType<NotFoundObjectResult>(await cb.Update(pkgA.Id, new UpdateTreatmentPackageDto(Status: "Cancelled")));
        Assert.IsType<NotFoundObjectResult>(await cb.RenewBookingLink(pkgA.Id));
        Assert.IsType<NotFoundObjectResult>(await cb.GetForBooking(bookingA));
        Assert.IsType<NotFoundObjectResult>(await cb.Create(new CreateTreatmentPackageDto(a.Customer.Id, a.Service.Id, 6)));
        Assert.Empty(OkValue<List<FollowUpDto>>(await cb.GetFollowUps()));

        using var check = TestDbContextFactory.Create(dbName);
        Assert.Equal(TreatmentPackageStatus.Active, (await check.TreatmentPackages.FindAsync(pkgA.Id))!.Status);
    }

    // ── Public link check ────────────────────────────────────────

    [Fact]
    public async Task PublicLinkCheck_ReturnsOnlyServiceInfo_AndRejectsForeignSlugOrStarter()
    {
        var dbName = Guid.NewGuid().ToString();
        using var db = TestDbContextFactory.Create(dbName);
        var a = SeedStudio(db, SubscriptionPlan.Professional, "Studio Link");
        var b = SeedStudio(db, SubscriptionPlan.Professional, "Studio Fremd");
        var pkg = await CreatePackage(db, a);
        var token = (await db.TreatmentPackages.FindAsync(pkg.Id))!.BookingToken;
        Assert.True(token.Length >= 40);

        var pub = new PublicBookingController(TestDbContextFactory.Create(dbName));
        var ok = Assert.IsType<OkObjectResult>(await pub.CheckPackageLink(a.Tenant.Slug, token));
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains(a.Service.Id.ToString(), json);
        Assert.Contains("\"valid\":true", json);
        Assert.DoesNotContain("Lena", json);
        Assert.DoesNotContain("Hauttyp", json);
        Assert.DoesNotContain("Kunde", json);

        Assert.IsType<NotFoundObjectResult>(await pub.CheckPackageLink(b.Tenant.Slug, token));
        Assert.IsType<NotFoundObjectResult>(await pub.CheckPackageLink(a.Tenant.Slug, "falsch"));

        var sub = await db.Subscriptions.FirstAsync(x => x.TenantId == a.Tenant.Id);
        sub.Plan = SubscriptionPlan.Starter;
        await db.SaveChangesAsync();
        Assert.IsType<NotFoundObjectResult>(await new PublicBookingController(TestDbContextFactory.Create(dbName)).CheckPackageLink(a.Tenant.Slug, token));
    }

    [Fact]
    public async Task RenewLink_InvalidatesOldToken()
    {
        var dbName = Guid.NewGuid().ToString();
        using var db = TestDbContextFactory.Create(dbName);
        var s = SeedStudio(db, SubscriptionPlan.Agency, "Studio Renew");
        var pkg = await CreatePackage(db, s);
        var oldToken = (await db.TreatmentPackages.AsNoTracking().FirstAsync(p => p.Id == pkg.Id)).BookingToken;

        var renewed = OkValue<TreatmentPackageDto>(await Controller(db, s.Tenant.Id).RenewBookingLink(pkg.Id));
        Assert.DoesNotContain(oldToken, renewed.BookingLinkPath);
        Assert.IsType<NotFoundObjectResult>(await new PublicBookingController(TestDbContextFactory.Create(dbName)).CheckPackageLink(s.Tenant.Slug, oldToken));
    }
}
