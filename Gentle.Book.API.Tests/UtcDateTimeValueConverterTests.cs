using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Gentle.Book.API.Tests;

// Found during live pre-launch testing: a note sent at 16:40 Europe/Berlin showed as 15:40 in
// the UI. EmployeeNote.CreatedAt is set via DateTime.UtcNow, but SQL Server's datetime2 columns
// carry no timezone info, so EF returns it with Kind=Unspecified on every subsequent read —
// System.Text.Json then serializes it without a trailing "Z", and the browser's `new Date(...)`
// parses that as local time instead of UTC. GentleBookDbContext.ConfigureConventions now forces
// every DateTime column back to Kind=Utc on read; this round-trips through two separate
// DbContext instances against the same in-memory store (same as two real HTTP requests would)
// so it actually exercises that read path, not just the original in-memory object reference.
public class UtcDateTimeValueConverterTests
{
    [Fact]
    public async Task DateTime_RoundTripsThroughDifferentContext_WithUtcKind()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = new Tenant { Name = "Salon UTC-Test", Slug = "salon-utc-test-" + Guid.NewGuid(), IsActive = true };
        var employee = new Employee { TenantId = tenant.Id, Name = "Tea Test", Username = "tea-" + Guid.NewGuid(), Role = "Employee" };
        var sentAt = DateTime.UtcNow;

        using (var writeDb = TestDbContextFactory.Create(dbName))
        {
            writeDb.Tenants.Add(tenant);
            writeDb.Employees.Add(employee);
            writeDb.EmployeeNotes.Add(new EmployeeNote
            {
                TenantId = tenant.Id,
                EmployeeId = employee.Id,
                EmployeeName = employee.Name,
                Subject = "Statustest",
                Message = "GB-NOTE-DEPLOY-02",
                CreatedAt = sentAt,
            });
            await writeDb.SaveChangesAsync();
        }

        using var readDb = TestDbContextFactory.Create(dbName);
        var reloaded = await readDb.EmployeeNotes.AsNoTracking().FirstAsync(n => n.Message == "GB-NOTE-DEPLOY-02");

        Assert.Equal(DateTimeKind.Utc, reloaded.CreatedAt.Kind);
        Assert.Equal(sentAt, reloaded.CreatedAt, TimeSpan.FromSeconds(1));
    }
}
