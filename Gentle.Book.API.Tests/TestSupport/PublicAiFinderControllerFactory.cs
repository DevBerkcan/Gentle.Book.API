using GentleBook.Api.Controllers;
using GentleBook.Api.Data;
using GentleBook.Api.Services;
using GentleBook.Api.Services.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gentle.Book.API.Tests.TestSupport;

/// <summary>
/// Builds a real, anonymous PublicAiFinderController with a real EF (in-memory) DbContext and
/// real (not mocked) service instances. No ClaimsPrincipal needed — the controller is
/// [AllowAnonymous] and resolves its tenant by slug instead of a JWT claim.
/// </summary>
public static class PublicAiFinderControllerFactory
{
    public static PublicAiFinderController Create(GentleBookDbContext db)
    {
        var engine = new ServiceFinderEngine(db);
        var knowledge = new KnowledgeRetrievalService(db);
        var usageMeter = new AiUsageMeter(db);
        var orchestrator = new AiOrchestrator(
            new NullAiProviderAdapter(), usageMeter, knowledge, db, NullLogger<AiOrchestrator>.Instance);

        var emailService = TestServiceFactory.CreateEmailService(db);
        var bookingService = new BookingService(db, NullLogger<BookingService>.Instance, emailService, new FakeBackgroundJobClient());
        var bookingDraftService = new BookingDraftService(db, bookingService);

        return new PublicAiFinderController(db, engine, orchestrator, bookingDraftService);
    }
}
