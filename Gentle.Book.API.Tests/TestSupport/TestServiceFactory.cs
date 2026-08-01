using GentleBook.Api.Data;
using GentleBook.Api.Options;
using GentleBook.Api.Services;
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
}
