using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GentleBook.Api.Data;

// Used by `dotnet ef migrations` — bypasses full Program.cs startup.
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<GentleBookDbContext>
{
    public GentleBookDbContext CreateDbContext(string[] args)
    {
        var opts = new DbContextOptionsBuilder<GentleBookDbContext>()
            .UseSqlServer("Server=.;Database=GentleBook_Design;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;

        return new GentleBookDbContext(opts);
    }
}
