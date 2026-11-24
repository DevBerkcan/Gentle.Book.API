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
using System.Text.Json;
using Xunit;

namespace Gentle.Book.API.Tests;

// Studio-Pass + "Freunde machen Freude" + Stempelpass on completed visits. The public booking with
// ?ref= runs in a SERIALIZABLE transaction (not supported by EF InMemory) — its rule
// (TryRecordReferralAsync) is tested directly here and end-to-end in the real-SQL acceptance run.
public class StudioPassReferralTests
{
    private sealed record Studio(Tenant Tenant, Service Service, TenantSettings Settings);

    private static Studio SeedStudio(GentleBookDbContext db, SubscriptionPlan plan, string name, bool referralEnabled = true)
    {
        var (tenant, _) = AgencyTenantFactory.Seed(db, plan, name: name);
        var settings = new TenantSettings
        {
            TenantId = tenant.Id, CompanyName = name, TimeZone = "Europe/Berlin",
            ReferralEnabled = referralEnabled, ReferralRewardText = "Nail-Art-Upgrade beim nächsten Besuch",
            Address = "Musterstraße 1, 12345 Musterstadt",
        };
        var service = new Service { TenantId = tenant.Id, Name = "Maniküre", DurationMinutes = 45, Price = 39m, IsActive = true };
        db.AddRange(settings, service);
        db.SaveChanges();
        return new Studio(tenant, service, settings);
    }

    private static Customer AddCustomer(GentleBookDbContext db, Studio s, string first, string? email, string? phone = null, string? notes = null)
    {
        var c = new Customer { TenantId = s.Tenant.Id, FirstName = first, LastName = "Test", Email = email, Phone = phone, Notes = notes };
        db.Customers.Add(c);
        db.SaveChanges();
        return c;
    }

    private static Booking AddBooking(GentleBookDbContext db, Studio s, Customer c, int days, BookingStatus status)
    {
        var b = new Booking
        {
            TenantId = s.Tenant.Id, CustomerId = c.Id, ServiceId = s.Service.Id,
            BookingDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(days)),
            StartTime = new TimeOnly(10, 0), EndTime = new TimeOnly(10, 45), Status = status,
        };
        db.Bookings.Add(b);
        db.SaveChanges();
        return b;
    }

    private static AdminService Admin(GentleBookDbContext db) =>
        new(db, NullLogger<AdminService>.Instance, TestServiceFactory.CreateEmailService(db),
            new LoyaltyService(db, NullLogger<LoyaltyService>.Instance), TestServiceFactory.CreateVoucherService(db),
            new FakeBackgroundJobClient(), new TenantContext());

    private static Task Complete(GentleBookDbContext db, Booking b) =>
        Admin(db).UpdateBookingStatusAsync(b.Id, new UpdateBookingStatusDto("Completed", null));

    /// <summary>Simulates "friend books for the first time via ?ref=CODE" (rule + booking, as BookingService does).</summary>
    private static async Task<(Customer friend, Booking booking, Referral? referral)> FriendBooks(
        GentleBookDbContext db, Studio s, string code, string first = "Anna", string? email = null, string? phone = null)
    {
        var friend = AddCustomer(db, s, first, email ?? $"{first.ToLower()}-{Guid.NewGuid():N}@example.de", phone);
        var booking = new Booking
        {
            TenantId = s.Tenant.Id, CustomerId = friend.Id, ServiceId = s.Service.Id,
            BookingDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(9, 45),
            Status = BookingStatus.Confirmed,
        };
        db.Bookings.Add(booking);
        var referral = await StudioPassRules.TryRecordReferralAsync(db, s.Tenant.Id, code, friend, booking);
        await db.SaveChangesAsync();
        return (friend, booking, referral);
    }

    private static async Task<string> CodeOf(GentleBookDbContext db, Customer c)
    {
        var code = await StudioPassRules.EnsureReferralCodeAsync(db, c);
        await db.SaveChangesAsync();
        return code;
    }

    private static async Task<string> TokenFor(GentleBookDbContext db, Customer c)
    {
        var token = StudioPassRules.EnsurePassToken(c);
        await db.SaveChangesAsync();
        return token;
    }

    private static T OkValue<T>(IActionResult r) => Assert.IsType<T>(Assert.IsType<OkObjectResult>(r).Value);
    private static JsonElement Json(IActionResult r) =>
        JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(r).Value);

    private static ReferralsController Referrals(GentleBookDbContext db, Guid tenantId, string role = "TenantAdmin")
    {
        var ctx = new TenantContext();
        ctx.Set(tenantId, "t", isSuperAdmin: false, role: role);
        var user = role == "Employee" ? ClaimsPrincipalFactory.Employee(Guid.NewGuid(), tenantId) : ClaimsPrincipalFactory.TenantAdmin(Guid.NewGuid(), tenantId);
        var http = new DefaultHttpContext { User = user };
        var audit = new AuditService(db, new HttpContextAccessor { HttpContext = http }, NullLogger<AuditService>.Instance);
        return new ReferralsController(db, ctx, audit) { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    private static PublicPassController Pass(GentleBookDbContext db) => new(db, TestServiceFactory.CreateEmailService(db));

    // ── The sales demo, end to end ───────────────────────────────

    [Fact]
    public async Task Demo_InviteFriend_FirstVisitUnlocksBonusForBoth_StudioSeesIt()
    {
        using var db = TestDbContextFactory.Create();
        var s = SeedStudio(db, SubscriptionPlan.Professional, "Nagelstudio Demo");
        var lisa = AddCustomer(db, s, "Lisa", "lisa@example.de");
        await Complete(db, AddBooking(db, s, lisa, -14, BookingStatus.Confirmed));
        var token = StudioPassRules.EnsurePassToken(lisa);
        await db.SaveChangesAsync();

        // Lisa's pass offers "Freundin einladen" after her completed visit.
        var pass = Json(await Pass(db).GetPass(token));
        Assert.True(pass.GetProperty("referral").GetProperty("canInvite").GetBoolean());
        var sharePath = pass.GetProperty("referral").GetProperty("sharePath").GetString()!;
        var code = sharePath.Split("ref=")[1];

        // Anna books via the link → pending; no bonus yet.
        var (anna, annaBooking, referral) = await FriendBooks(db, s, code);
        Assert.NotNull(referral);
        Assert.Equal(ReferralStatus.Pending, (await db.Referrals.FindAsync(referral!.Id))!.Status);
        Assert.Empty(Json(await Pass(db).GetPass(token)).GetProperty("rewards").EnumerateArray());

        // Anna's first appointment is completed → bonus for both.
        await Complete(db, annaBooking);
        var r = await db.Referrals.FindAsync(referral.Id);
        Assert.Equal(ReferralStatus.Qualified, r!.Status);
        Assert.Equal("Nail-Art-Upgrade beim nächsten Besuch", r.RewardText);
        var lisaRewards = Json(await Pass(db).GetPass(token)).GetProperty("rewards").EnumerateArray().ToList();
        Assert.Single(lisaRewards);
        Assert.Equal("referral_thanks", lisaRewards[0].GetProperty("kind").GetString());
        var annaPass = Json(await Pass(db).GetPass(await TokenFor(db, anna)));
        Assert.Equal("referral_welcome", annaPass.GetProperty("rewards")[0].GetProperty("kind").GetString());

        // Studio dashboard.
        var stats = Json(await Referrals(db, s.Tenant.Id).GetStats());
        Assert.Equal(1, stats.GetProperty("bookedInvitations").GetInt32());
        Assert.Equal(1, stats.GetProperty("firstVisits").GetInt32());
        Assert.Equal(2, stats.GetProperty("rewardsEarned").GetInt32());
        Assert.Equal(0, stats.GetProperty("rewardsRedeemed").GetInt32());
        Assert.Equal(0, stats.GetProperty("secondBookings").GetInt32());

        // Redeem both, Anna books again → second booking counted.
        await Referrals(db, s.Tenant.Id).Redeem(r.Id, new RedeemReferralRewardDto("referrer"));
        await Referrals(db, s.Tenant.Id).Redeem(r.Id, new RedeemReferralRewardDto("referred"));
        AddBooking(db, s, anna, 10, BookingStatus.Confirmed);
        stats = Json(await Referrals(db, s.Tenant.Id).GetStats());
        Assert.Equal(2, stats.GetProperty("rewardsRedeemed").GetInt32());
        Assert.Equal(1, stats.GetProperty("secondBookings").GetInt32());
        Assert.True(Json(await Pass(db).GetPass(token)).GetProperty("rewards")[0].GetProperty("redeemed").GetBoolean());
    }

    // ── Referral rules ───────────────────────────────────────────

    [Fact]
    public async Task Referral_IsIgnored_ForSelfSameContactExistingCustomerForeignCodeDisabledOrStarter()
    {
        using var db = TestDbContextFactory.Create();
        var s = SeedStudio(db, SubscriptionPlan.Agency, "Studio Regeln");
        var lisa = AddCustomer(db, s, "Lisa", "lisa@example.de", "0151 1234567");
        var code = await CodeOf(db, lisa);

        // Same e-mail / same phone as the referrer under a second record.
        Assert.Null((await FriendBooks(db, s, code, "Zweit", email: "LISA@example.de")).referral);
        Assert.Null((await FriendBooks(db, s, code, "Phone", phone: "+49 151 1234567".Replace("+49 ", "0"))).referral);

        // Self (referrer books via own link).
        var selfBooking = new Booking { TenantId = s.Tenant.Id, CustomerId = lisa.Id, ServiceId = s.Service.Id, BookingDate = DateOnly.FromDateTime(DateTime.UtcNow), StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(8, 45) };
        db.Bookings.Add(selfBooking);
        Assert.Null(await StudioPassRules.TryRecordReferralAsync(db, s.Tenant.Id, code, lisa, selfBooking));

        // Existing customer with an earlier booking.
        var regular = AddCustomer(db, s, "Stamm", "stamm@example.de");
        AddBooking(db, s, regular, -30, BookingStatus.Completed);
        var b = new Booking { TenantId = s.Tenant.Id, CustomerId = regular.Id, ServiceId = s.Service.Id, BookingDate = DateOnly.FromDateTime(DateTime.UtcNow), StartTime = new TimeOnly(11, 0), EndTime = new TimeOnly(11, 45) };
        db.Bookings.Add(b);
        Assert.Null(await StudioPassRules.TryRecordReferralAsync(db, s.Tenant.Id, code, regular, b));

        // Unknown / lowercase-normalized code.
        Assert.Null((await FriendBooks(db, s, "XXXXXXXX", "Unbekannt")).referral);
        Assert.NotNull((await FriendBooks(db, s, code.ToLowerInvariant(), "Klein")).referral);

        // Code of another studio.
        var other = SeedStudio(db, SubscriptionPlan.Agency, "Studio Fremd");
        Assert.Null((await FriendBooks(db, other, code, "Fremd")).referral);

        // Disabled program / Starter plan.
        var off = SeedStudio(db, SubscriptionPlan.Agency, "Studio Aus", referralEnabled: false);
        var offCode = await CodeOf(db, AddCustomer(db, off, "Off", "off@example.de"));
        Assert.Null((await FriendBooks(db, off, offCode, "OffFriend")).referral);
        var starter = SeedStudio(db, SubscriptionPlan.Starter, "Studio Starter");
        var starterCode = await CodeOf(db, AddCustomer(db, starter, "St", "st@example.de"));
        Assert.Null((await FriendBooks(db, starter, starterCode, "StFriend")).referral);
    }

    [Fact]
    public async Task Qualification_OnlyOnCompletion_Once_AndCancelledFirstBookingStillQualifiesLater()
    {
        using var db = TestDbContextFactory.Create();
        var s = SeedStudio(db, SubscriptionPlan.Professional, "Studio Quali");
        var lisa = AddCustomer(db, s, "Lisa", "lisa@example.de");
        var (anna, first, referral) = await FriendBooks(db, s, await CodeOf(db, lisa));

        await Admin(db).UpdateBookingStatusAsync(first.Id, new UpdateBookingStatusDto("Cancelled", null));
        Assert.Equal(ReferralStatus.Pending, (await db.Referrals.FindAsync(referral!.Id))!.Status);

        var second = AddBooking(db, s, anna, -1, BookingStatus.Confirmed);
        await Complete(db, second);
        var r = (await db.Referrals.FindAsync(referral.Id))!;
        Assert.Equal(ReferralStatus.Qualified, r.Status);
        Assert.Equal(second.Id, r.QualifyingBookingId);
        var qualifiedAt = r.QualifiedAt;

        // Studio changes the reward afterwards; re-completing / another visit doesn't touch the earned one.
        s.Settings.ReferralRewardText = "Etwas anderes";
        await db.SaveChangesAsync();
        await Complete(db, second);
        await Complete(db, AddBooking(db, s, anna, -2, BookingStatus.Confirmed));
        r = (await db.Referrals.AsNoTracking().FirstAsync(x => x.Id == referral.Id));
        Assert.Equal(qualifiedAt, r.QualifiedAt);
        Assert.Equal("Nail-Art-Upgrade beim nächsten Besuch", r.RewardText);
    }

    // ── Stempelpass counts completed visits ──────────────────────

    [Fact]
    public async Task StampReward_IssuedOnThirdCompletedVisit_NotOnBookings_NoDuplicates()
    {
        using var db = TestDbContextFactory.Create();
        var s = SeedStudio(db, SubscriptionPlan.Agency, "Studio Stempel");
        s.Settings.LoyaltyRewardEveryNVisits = 3;
        s.Settings.LoyaltyRewardType = "MonetaryValue";
        s.Settings.LoyaltyRewardValue = 10m;
        await db.SaveChangesAsync();
        var c = AddCustomer(db, s, "Nina", email: null); // no e-mail → no SMTP in tests

        var b1 = AddBooking(db, s, c, -20, BookingStatus.Confirmed);
        var b2 = AddBooking(db, s, c, -10, BookingStatus.Confirmed);
        var b3 = AddBooking(db, s, c, -1, BookingStatus.Confirmed);
        AddBooking(db, s, c, 5, BookingStatus.Confirmed); // 4th booking, not yet a visit
        Assert.Equal(0, await db.Vouchers.CountAsync(v => v.CustomerId == c.Id)); // bookings alone earn nothing

        await Complete(db, b1);
        await Complete(db, b2);
        Assert.Equal(0, await db.Vouchers.CountAsync(v => v.CustomerId == c.Id));
        await Complete(db, b3);
        await Complete(db, b3); // re-save
        await db.Vouchers.IgnoreQueryFilters().ToListAsync();
        var vouchers = await db.Vouchers.Where(v => v.CustomerId == c.Id).ToListAsync();
        Assert.Single(vouchers);
        Assert.Equal(10m, vouchers[0].RemainingAmount);

        // A cancelled booking never counts as a stamp.
        var cancelled = AddBooking(db, s, c, 7, BookingStatus.Confirmed);
        await Admin(db).UpdateBookingStatusAsync(cancelled.Id, new UpdateBookingStatusDto("Cancelled", null));
        Assert.Equal(3, await VoucherService.CountCompletedVisitsAsync(db, c.Id));

        var pass = Json(await Pass(db).GetPass(await TokenFor(db, c)));
        Assert.Equal(3, pass.GetProperty("stamps").GetProperty("target").GetInt32());
        Assert.Equal(0, pass.GetProperty("stamps").GetProperty("current").GetInt32());
        Assert.Equal("10 € Gutschein", pass.GetProperty("stamps").GetProperty("rewardLabel").GetString());
        Assert.Single(pass.GetProperty("vouchers").EnumerateArray());
    }

    // ── Pass: data minimisation, links, isolation ────────────────

    [Fact]
    public async Task Pass_ShowsOnlyOwnFirstNameAndAppointments_NoInternalData()
    {
        using var db = TestDbContextFactory.Create();
        var s = SeedStudio(db, SubscriptionPlan.Professional, "Studio Pass");
        var c = AddCustomer(db, s, "Lena", "lena-geheim@example.de", "0170 999", notes: "INTERN: Allergie");
        var other = AddCustomer(db, s, "Fremde", "fremd@example.de");
        AddBooking(db, s, c, 3, BookingStatus.Confirmed);
        AddBooking(db, s, c, 9, BookingStatus.Cancelled);
        AddBooking(db, s, other, 4, BookingStatus.Confirmed);
        var token = StudioPassRules.EnsurePassToken(c);
        await db.SaveChangesAsync();

        var result = await Pass(db).GetPass(token);
        var raw = JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Contains("Lena", raw);
        Assert.DoesNotContain("lena-geheim", raw);
        Assert.DoesNotContain("0170 999", raw);
        Assert.DoesNotContain("Allergie", raw);
        Assert.DoesNotContain("Fremde", raw);

        var pass = Json(result);
        var upcoming = pass.GetProperty("upcoming").EnumerateArray().ToList();
        Assert.Single(upcoming); // own, not cancelled, not the other customer's
        Assert.StartsWith("/booking/cancel/", upcoming[0].GetProperty("managePath").GetString());
        Assert.Equal($"/booking/{s.Tenant.Slug}/book", pass.GetProperty("studio").GetProperty("bookingPath").GetString());
        Assert.False(pass.GetProperty("referral").GetProperty("canInvite").GetBoolean()); // no completed visit yet

        Assert.IsType<NotFoundResult>(await Pass(db).GetPass("falsches-token"));
    }

    [Fact]
    public async Task Pass_ShowsOpenPackageWithNextSessionLink()
    {
        using var db = TestDbContextFactory.Create();
        var s = SeedStudio(db, SubscriptionPlan.Professional, "Laser Pass");
        var c = AddCustomer(db, s, "Laura", "laura@example.de");
        var pkg = new TreatmentPackage { TenantId = s.Tenant.Id, CustomerId = c.Id, ServiceId = s.Service.Id, Name = "Laser Achseln – 6 Sitzungen", TotalSessions = 6, PreviouslyUsedSessions = 3, BookingToken = TreatmentPackageRules.NewBookingToken() };
        db.TreatmentPackages.Add(pkg);
        var token = StudioPassRules.EnsurePassToken(c);
        await db.SaveChangesAsync();

        var p = Json(await Pass(db).GetPass(token)).GetProperty("packages")[0];
        Assert.Equal(3, p.GetProperty("openSessions").GetInt32());
        Assert.Contains($"packageToken={Uri.EscapeDataString(pkg.BookingToken)}", p.GetProperty("nextSessionBookingPath").GetString());
    }

    [Fact]
    public async Task Pass_StopsWorking_AfterGdprErasure_AndAfterRenew()
    {
        using var db = TestDbContextFactory.Create();
        var s = SeedStudio(db, SubscriptionPlan.Professional, "Studio DSGVO");
        var c = AddCustomer(db, s, "Mia", "mia@example.de");
        var first = Json(await Referrals(db, s.Tenant.Id).GetForCustomer(c.Id)).GetProperty("passPath").GetString()!;
        var oldToken = first.Split('/').Last();
        Assert.IsType<OkObjectResult>(await Pass(db).GetPass(oldToken));

        var renewed = Json(await Referrals(db, s.Tenant.Id).RenewPass(c.Id)).GetProperty("passPath").GetString()!;
        Assert.NotEqual(first, renewed);
        Assert.IsType<NotFoundResult>(await Pass(db).GetPass(oldToken));

        var newToken = renewed.Split('/').Last();
        var customerService = new CustomerService(db, NullLogger<CustomerService>.Instance, new TenantContext(), TestServiceFactory.CreateEmailService(db));
        await customerService.DeleteCustomerAsync(c.Id, null, isAdmin: true);
        Assert.IsType<NotFoundResult>(await Pass(db).GetPass(newToken));
    }

    // ── Studio side: roles, plan, isolation, validation ──────────

    [Fact]
    public async Task StudioSide_PlanRoleIsolationAndValidation()
    {
        var dbName = Guid.NewGuid().ToString();
        Studio a, b, starter;
        Guid referralId;
        using (var seed = TestDbContextFactory.Create(dbName))
        {
            a = SeedStudio(seed, SubscriptionPlan.Agency, "Studio A");
            b = SeedStudio(seed, SubscriptionPlan.Agency, "Studio B");
            starter = SeedStudio(seed, SubscriptionPlan.Starter, "Studio S");
            var lisa = AddCustomer(seed, a, "Lisa", "lisa@a.de");
            var (_, booking, referral) = await FriendBooks(seed, a, await CodeOf(seed, lisa));
            await Complete(seed, booking);
            referralId = referral!.Id;
        }

        using var dbB = ScopedDb(dbName, b.Tenant.Id);
        Assert.IsType<NotFoundObjectResult>(await Referrals(dbB, b.Tenant.Id).Redeem(referralId, new RedeemReferralRewardDto("referrer")));
        Assert.Equal(0, Json(await Referrals(dbB, b.Tenant.Id).GetStats()).GetProperty("bookedInvitations").GetInt32());

        using var dbA = ScopedDb(dbName, a.Tenant.Id);
        Assert.IsType<ForbidResult>(await Referrals(dbA, a.Tenant.Id, "Employee").GetStats());
        Assert.IsType<ForbidResult>(await Referrals(dbA, a.Tenant.Id, "Employee").UpdateSettings(new UpdateReferralSettingsDto(false, null)));
        Assert.IsType<BadRequestObjectResult>(await Referrals(dbA, a.Tenant.Id).Redeem(referralId, new RedeemReferralRewardDto("beide")));
        Assert.IsType<BadRequestObjectResult>(await Referrals(dbA, a.Tenant.Id).UpdateSettings(new UpdateReferralSettingsDto(true, "  ")));
        Assert.IsType<BadRequestObjectResult>(await Referrals(dbA, a.Tenant.Id).UpdateSettings(new UpdateReferralSettingsDto(true, new string('x', 301))));
        Assert.IsType<OkObjectResult>(await Referrals(dbA, a.Tenant.Id).UpdateSettings(new UpdateReferralSettingsDto(true, "Gratis Pflege-Upgrade")));

        using var dbS = ScopedDb(dbName, starter.Tenant.Id);
        Assert.Equal(402, Assert.IsType<ObjectResult>(await Referrals(dbS, starter.Tenant.Id).GetStats()).StatusCode);
        Assert.Equal(402, Assert.IsType<ObjectResult>(await Referrals(dbS, starter.Tenant.Id).UpdateSettings(new UpdateReferralSettingsDto(true, "X"))).StatusCode);
        // Pass link itself works on every plan.
        var starterCustomer = AddCustomer(dbS, starter, "Sara", "sara@s.de");
        Assert.StartsWith("/pass/", Json(await Referrals(dbS, starter.Tenant.Id).GetForCustomer(starterCustomer.Id)).GetProperty("passPath").GetString());
    }

    private static GentleBookDbContext ScopedDb(string dbName, Guid tenantId)
    {
        var ctx = new TenantContext();
        ctx.Set(tenantId, "t", isSuperAdmin: false, role: "TenantAdmin");
        return new GentleBookDbContext(new DbContextOptionsBuilder<GentleBookDbContext>().UseInMemoryDatabase(dbName).Options, ctx);
    }
}
