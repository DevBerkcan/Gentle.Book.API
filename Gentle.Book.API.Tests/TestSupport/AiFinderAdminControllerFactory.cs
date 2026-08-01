using GentleBook.Api.Controllers;
using GentleBook.Api.Data;
using GentleBook.Api.Services;
using GentleBook.Api.Services.AI;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gentle.Book.API.Tests.TestSupport;

/// <summary>
/// Builds a real AiFinderAdminController with a real EF (in-memory) DbContext and a real
/// TenantContext set to the given tenant/role, and real (not mocked) AI service instances —
/// same "real everything except Hangfire/HTTP" approach as TenantControllerFactory.
/// </summary>
public static class AiFinderAdminControllerFactory
{
    public static AiFinderAdminController Create(GentleBookDbContext db, Guid? tenantId, string? role)
    {
        var tenantContext = new TenantContext();
        tenantContext.Set(tenantId, "test-tenant", isSuperAdmin: false, role);

        var engine = new ServiceFinderEngine(db);
        var knowledge = new KnowledgeRetrievalService(db);
        var usageMeter = new AiUsageMeter(db);
        var orchestrator = new AiOrchestrator(
            new NullAiProviderAdapter(), usageMeter, knowledge, db, NullLogger<AiOrchestrator>.Instance);

        var controller = new AiFinderAdminController(db, tenantContext, engine, orchestrator);

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
