using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gentle.Book.API.Tests;

// Found during live pre-launch testing: the public booking widget offered times outside the
// employee's actual working hours (and on days the employee doesn't work at all), ignoring the
// employee's configured schedule entirely. Root cause: IsTimeSlotAvailableForEmployeeAsync — the
// path used whenever no specific employee is pre-selected yet, i.e. the normal public booking
// flow — only checked existing bookings and blocked slots, never EmployeeSchedule or
// EmployeeVacations at all (unlike DetermineAvailableSlotsForEmployee, the parallel path used
// once an employee IS selected, which already did this correctly).
public class AvailabilityServiceEmployeeScheduleTests
{
    private static AvailabilityService Build(GentleBook.Api.Data.GentleBookDbContext db) =>
        new(db, NullLogger<AvailabilityService>.Instance, new TenantContext());

    private static (Tenant tenant, Employee employee) SeedEmployeeWithMonFriSchedule(GentleBook.Api.Data.GentleBookDbContext db)
    {
        var tenant = new Tenant { Name = "Salon Hours", Slug = "salon-hours-" + Guid.NewGuid(), IsActive = true };
        db.Tenants.Add(tenant);
        var employee = new Employee { TenantId = tenant.Id, Name = "Berk-Can A.", IsActive = true };
        db.Employees.Add(employee);

        foreach (var day in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday })
        {
            db.EmployeeSchedules.Add(new EmployeeSchedule
            {
                TenantId = tenant.Id,
                EmployeeId = employee.Id,
                DayOfWeek = day,
                IsWorkingDay = true,
                StartTime = new TimeOnly(9, 0),
                EndTime = new TimeOnly(18, 0),
            });
        }
        db.EmployeeSchedules.Add(new EmployeeSchedule
        {
            TenantId = tenant.Id,
            EmployeeId = employee.Id,
            DayOfWeek = DayOfWeek.Saturday,
            IsWorkingDay = false,
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(18, 0),
        });
        db.SaveChanges();
        return (tenant, employee);
    }

    private static DateOnly NextDateFor(DayOfWeek dow)
    {
        var date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1));
        while (date.DayOfWeek != dow) date = date.AddDays(1);
        return date;
    }

    [Fact]
    public async Task IsTimeSlotAvailable_BeforeEmployeeStartTime_ReturnsFalse()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, employee) = SeedEmployeeWithMonFriSchedule(db);
        var service = Build(db);
        var monday = NextDateFor(DayOfWeek.Monday);

        var available = await service.IsTimeSlotAvailableForEmployeeAsync(
            monday, new TimeOnly(8, 0), new TimeOnly(8, 30), employee.Id, tenant.Id);

        Assert.False(available);
    }

    [Fact]
    public async Task IsTimeSlotAvailable_AfterEmployeeEndTime_ReturnsFalse()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, employee) = SeedEmployeeWithMonFriSchedule(db);
        var service = Build(db);
        var monday = NextDateFor(DayOfWeek.Monday);

        var available = await service.IsTimeSlotAvailableForEmployeeAsync(
            monday, new TimeOnly(19, 0), new TimeOnly(19, 30), employee.Id, tenant.Id);

        Assert.False(available);
    }

    [Fact]
    public async Task IsTimeSlotAvailable_OnNonWorkingSaturday_ReturnsFalse()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, employee) = SeedEmployeeWithMonFriSchedule(db);
        var service = Build(db);
        var saturday = NextDateFor(DayOfWeek.Saturday);

        var available = await service.IsTimeSlotAvailableForEmployeeAsync(
            saturday, new TimeOnly(11, 0), new TimeOnly(11, 30), employee.Id, tenant.Id);

        Assert.False(available);
    }

    [Fact]
    public async Task IsTimeSlotAvailable_WithinEmployeeHours_ReturnsTrue()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, employee) = SeedEmployeeWithMonFriSchedule(db);
        var service = Build(db);
        var monday = NextDateFor(DayOfWeek.Monday);

        var available = await service.IsTimeSlotAvailableForEmployeeAsync(
            monday, new TimeOnly(11, 0), new TimeOnly(11, 30), employee.Id, tenant.Id);

        Assert.True(available);
    }

    [Fact]
    public async Task IsTimeSlotAvailable_EmployeeOnVacation_ReturnsFalse()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, employee) = SeedEmployeeWithMonFriSchedule(db);
        var monday = NextDateFor(DayOfWeek.Monday);
        db.EmployeeVacations.Add(new EmployeeVacation
        {
            TenantId = tenant.Id,
            EmployeeId = employee.Id,
            StartDate = monday,
            EndDate = monday,
        });
        db.SaveChanges();
        var service = Build(db);

        var available = await service.IsTimeSlotAvailableForEmployeeAsync(
            monday, new TimeOnly(11, 0), new TimeOnly(11, 30), employee.Id, tenant.Id);

        Assert.False(available);
    }

    [Fact]
    public async Task IsTimeSlotAvailable_NoScheduleConfiguredAtAll_FallsBackToAvailable()
    {
        // An employee who never touched the schedule feature at all (no rows for any day)
        // keeps the legacy permissive behavior — only bookings/blocked slots matter — so
        // existing tenants that never configured this aren't suddenly unbookable everywhere.
        using var db = TestDbContextFactory.Create();
        var tenant = new Tenant { Name = "Salon NoSchedule", Slug = "salon-no-schedule-" + Guid.NewGuid(), IsActive = true };
        db.Tenants.Add(tenant);
        var employee = new Employee { TenantId = tenant.Id, Name = "Kim A.", IsActive = true };
        db.Employees.Add(employee);
        db.SaveChanges();
        var service = Build(db);
        var monday = NextDateFor(DayOfWeek.Monday);

        var available = await service.IsTimeSlotAvailableForEmployeeAsync(
            monday, new TimeOnly(8, 0), new TimeOnly(8, 30), employee.Id, tenant.Id);

        Assert.True(available);
    }
}
