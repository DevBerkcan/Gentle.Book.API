using GentleBook.Api.Data;
using GentleBook.Api.Options;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Gentle.Book.API.Tests.TestSupport;

/// <summary>
/// Builds real (not mocked) service instances for controller tests, with harmless no-op
/// configuration (blank SMTP settings etc.) — sufficient because the endpoints under test
/// never actually trigger an SMTP send; if they did, MailKit would simply fail to connect,
/// which is the correct failure mode for a test that accidentally exercises that path.
/// </summary>
public static class TestServiceFactory
{
    public static EmailService CreateEmailService(GentleBookDbContext db)
    {
        var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        return new EmailService(
            Options.Create(new EmailOptions()),
            Options.Create(new InvoiceEmailOptions()),
            db,
            NullLogger<EmailService>.Instance,
            scopeFactory,
            TestConfiguration.Build());
    }

    public static VoucherService CreateVoucherService(GentleBookDbContext db) =>
        new(db, CreateEmailService(db), NullLogger<VoucherService>.Instance);

    /// <summary>
    /// SubscriptionService creates its own DbContext/IWebHostEnvironment via IServiceScopeFactory
    /// internally (same reasoning as AiBudgetServiceTests) — this wires a small real DI container
    /// around the SAME in-memory db instance (AddSingleton) so an on-demand purge in a test
    /// actually lands in the db the test can assert against, instead of a disconnected one.
    /// </summary>
    public static SubscriptionService CreateSubscriptionService(GentleBookDbContext db)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton<IWebHostEnvironment>(new FakeWebHostEnvironment());
        services.AddSingleton(CreateEmailService(db));
        var provider = services.BuildServiceProvider();

        return new SubscriptionService(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<SubscriptionService>.Instance);
    }

    public static DataSubjectRequestService CreateDataSubjectRequestService(GentleBookDbContext db)
    {
        var httpContextAccessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var auditService = new AuditService(db, httpContextAccessor, NullLogger<AuditService>.Instance);

        return new DataSubjectRequestService(
            db, CreateSubscriptionService(db), CreateEmailService(db), auditService, NullLogger<DataSubjectRequestService>.Instance);
    }
}
