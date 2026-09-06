using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Controllers;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Options;
using GentleBook.Api.Services;
using GentleBook.Api.Services.AI;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gentle.Book.API.Tests;

// Covers the "AI tagline suggestions" Agency-exclusive feature (EmployeesController.SuggestTagline)
// — had zero test coverage before this pass. OpenAiClient is a thin HttpClient wrapper (same
// pattern as MollieClient), so its outbound call is faked via FakeHttpMessageHandler rather than
// needing a separate interface — no live OpenAI call happens in these tests.
public class EmployeesControllerAiTaglineTests
{
    private static EmployeesController BuildController(
        GentleBook.Api.Data.GentleBookDbContext db, Guid tenantId, string aiApiKey, Func<HttpRequestMessage, HttpResponseMessage>? responder = null)
    {
        var tenantContext = new TenantContext();
        tenantContext.Set(tenantId, "test-tenant", role: "TenantAdmin");

        var employeeService = new EmployeeService(db, NullLogger<EmployeeService>.Instance, tenantContext);
        var httpContextAccessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var audit = new AuditService(db, httpContextAccessor, NullLogger<AuditService>.Instance);
        var aiOptions = Options.Create(new AiProviderOptions { ApiKey = aiApiKey });
        var openAiClient = new OpenAiClient(
            new HttpClient(new FakeHttpMessageHandler(responder ?? (_ => throw new InvalidOperationException("OpenAI should not be called in this test.")))) { BaseAddress = new Uri("https://api.openai.com/v1/") },
            new StaticOptionsMonitor<AiProviderOptions>(new AiProviderOptions { ApiKey = aiApiKey }));
        var aiUsageMeter = new AiUsageMeter(db);

        var controller = new EmployeesController(
            employeeService, db, TestConfiguration.Build(), audit, new FakeWebHostEnvironment(),
            NullLogger<EmployeesController>.Instance, openAiClient, aiUsageMeter, aiOptions);

        // SuggestTagline resolves ITenantContext from HttpContext.RequestServices rather than a
        // constructor field — wire a minimal service provider so that resolution succeeds.
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(tenantContext);
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = ClaimsPrincipalFactory.TenantAdmin(Guid.NewGuid(), tenantId),
        };
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static Employee SeedEmployee(GentleBook.Api.Data.GentleBookDbContext db, Guid tenantId)
    {
        var employee = new Employee { TenantId = tenantId, Name = "Ada", Role = "Friseurin", Specialty = "Balayage" };
        db.Employees.Add(employee);
        db.SaveChanges();
        return employee;
    }

    private static HttpResponseMessage ChatCompletionResponse(string content)
    {
        var body = "{\"model\":\"gpt-4o-mini\",\"choices\":[{\"message\":{\"content\":\"" + content + "\"}}],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":4}}";
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
    }

    [Fact]
    public async Task SuggestTagline_NonAgencyPlan_Returns402()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = AgencyTenantFactory.Seed(db, SubscriptionPlan.Professional);
        var employee = SeedEmployee(db, tenant.Id);
        var controller = BuildController(db, tenant.Id, aiApiKey: "test-key");

        var result = await controller.SuggestTagline(employee.Id);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(402, objectResult.StatusCode);
    }

    [Fact]
    public async Task SuggestTagline_AgencyPlanNoApiKeyConfigured_Returns503()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = AgencyTenantFactory.Seed(db, SubscriptionPlan.Agency);
        var employee = SeedEmployee(db, tenant.Id);
        var controller = BuildController(db, tenant.Id, aiApiKey: "");

        var result = await controller.SuggestTagline(employee.Id);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, objectResult.StatusCode);
    }

    [Fact]
    public async Task SuggestTagline_AgencyPlanConfigured_ReturnsAiSuggestion()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = AgencyTenantFactory.Seed(db, SubscriptionPlan.Agency);
        var employee = SeedEmployee(db, tenant.Id);
        var controller = BuildController(db, tenant.Id, aiApiKey: "test-key", _ => ChatCompletionResponse("herzlich, praezise, kreativ"));

        var result = await controller.SuggestTagline(employee.Id);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("herzlich", json);
    }
}
