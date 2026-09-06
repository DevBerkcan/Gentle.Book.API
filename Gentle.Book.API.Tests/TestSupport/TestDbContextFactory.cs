using GentleBook.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Gentle.Book.API.Tests.TestSupport;

/// <summary>
/// Every test gets its own isolated in-memory database (unique name per call) so tests never
/// leak state into each other, without needing a real SQL Server instance.
/// </summary>
public static class TestDbContextFactory
{
    public static GentleBookDbContext Create()
    {
        return Create(Guid.NewGuid().ToString());
    }

    /// <summary>
    /// Creates a second, independently-tracked GentleBookDbContext against the SAME named
    /// in-memory store — for tests simulating two concurrent requests, which in production each
    /// get their own scoped DbContext hitting the same real database.
    /// </summary>
    public static GentleBookDbContext Create(string dbName)
    {
        var options = new DbContextOptionsBuilder<GentleBookDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        return new GentleBookDbContext(options);
    }
}
