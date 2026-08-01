using GentleBook.Api.Controllers;
using GentleBook.Api.Data;
using GentleBook.Api.Options;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Gentle.Book.API.Tests.TestSupport;

public static class TenantControllerFactory
{
    /// <summary>
    /// Builds a real TenantController with a real EF (in-memory) DbContext and a real
    /// TenantContext set to the given tenant/role — everything downstream of Mollie is a
    /// harmless fake, since none of the endpoints under test (settings/plan-pricing/usage)
    /// call out to Mollie.
    /// </summary>
    public static TenantController Create(GentleBookDbContext db, Guid? tenantId, string? role, bool isSuperAdmin = false)
    {
        var tenantContext = new TenantContext();
        tenantContext.Set(tenantId, "test-tenant", isSuperAdmin, role);

        var emailService = TestServiceFactory.CreateEmailService(db);
        var httpContextAccessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var auditService = new AuditService(db, httpContextAccessor, NullLogger<AuditService>.Instance);

        var mollieOptions = Options.Create(new MollieOptions());
        var mollieClient = new MollieClient(new HttpClient(), new StaticOptionsMonitor<MollieOptions>(new MollieOptions()));
        var mollieService = new MollieService(
            db, mollieClient, mollieOptions, new FakeBackgroundJobClient(), auditService, emailService, NullLogger<MollieService>.Instance);

        var controller = new TenantController(
            db, tenantContext, emailService, NullLogger<TenantController>.Instance,
            new FakeWebHostEnvironment(), auditService, mollieService, mollieOptions);

        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = tenantId.HasValue
                    ? (role == "TenantAdmin"
                        ? ClaimsPrincipalFactory.TenantAdmin(Guid.NewGuid(), tenantId.Value)
                        : ClaimsPrincipalFactory.Employee(Guid.NewGuid(), tenantId.Value))
                    : ClaimsPrincipalFactory.SuperAdmin(Guid.NewGuid()),
            },
        };

        return controller;
    }
}
