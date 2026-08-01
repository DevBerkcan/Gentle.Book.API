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
        var options = new DbContextOptionsBuilder<GentleBookDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new GentleBookDbContext(options);
    }
}
