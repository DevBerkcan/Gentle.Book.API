using System.Net;
using GentleBook.Api.Controllers;
using GentleBook.Api.Data;
using GentleBook.Api.Services;
using GentleBook.Api.Services.AI;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gentle.Book.API.Tests.TestSupport;

/// <summary>
/// Builds a real, anonymous PublicAiChatbotController with a real EF (in-memory) DbContext and
/// real (not mocked) service instances — same pattern as PublicAiFinderControllerFactory.
/// AvailabilityService/BookingDraftService are the same real, unchanged services the click-wizard
/// uses, wired with an anonymous (no TenantId) TenantContext so they resolve the tenant by the
/// slug passed into each call, exactly as they do for real anonymous public-booking requests.
/// A fixed RemoteIpAddress is set on the ControllerContext (overridable via ipAddress) since
/// PublicAiChatbotController.ComputeVisitorHash reads HttpContext.Connection.RemoteIpAddress for
/// the per-visitor-per-day message cap — calls made on the same controller instance therefore
/// simulate "same visitor," matching how a real browser session behaves across repeated calls.
/// </summary>
public static class PublicAiChatbotControllerFactory
{
    public static PublicAiChatbotController Create(GentleBookDbContext db, string ipAddress = "203.0.113.10") =>
        CreateWithProvider(db, new NullAiProviderAdapter(), ipAddress);

    public static PublicAiChatbotController CreateWithProvider(GentleBookDbContext db, IAiProviderAdapter provider, string ipAddress = "203.0.113.10")
    {
        var engine = new ServiceFinderEngine(db);
        var knowledge = new KnowledgeRetrievalService(db);
        var usageMeter = new AiUsageMeter(db);
        var usageGate = new AiUsageGate(db);
        var orchestrator = new AiOrchestrator(
            provider, usageMeter, usageGate, knowledge, db, NullLogger<AiOrchestrator>.Instance);

        var anonymousTenantContext = new TenantContext();
        var availability = new AvailabilityService(db, NullLogger<AvailabilityService>.Instance, anonymousTenantContext);

        var emailService = TestServiceFactory.CreateEmailService(db);
        var voucherService = TestServiceFactory.CreateVoucherService(db);
        var bookingService = new BookingService(db, NullLogger<BookingService>.Instance, emailService, new FakeBackgroundJobClient(), voucherService);
        var bookingDraftService = new BookingDraftService(db, bookingService);

        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse(ipAddress);

        return new PublicAiChatbotController(db, engine, orchestrator, availability, bookingDraftService)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }
}
