using GentleBook.Api.Configuration;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.DTOs;
using GentleBook.Api.Services;
using GentleBook.Api.Services.AI;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Controllers;

// Chat booking assistant widget — Agency-exclusive, shown bottom-right on the public booking
// page alongside (never instead of) the existing click-through wizard, gated additionally by
// the tenant's own TenantSettings.IsChatbotEnabled toggle in the Links/Design settings.
//
// One endpoint drives the whole conversation (free-text service recommendation through to a
// confirmed booking). The client (ChatbotWidget.tsx) holds the accumulated selection in its own
// React state and resends whatever's relevant on each call — SendMessage branches on which
// optional fields are populated, in the priority order that matches the natural flow: confirm a
// draft > create/describe a draft (service+slot[+customer]) > list slots for a chosen
// employee > list employees for a chosen service > fall back to free-text recommendation.
// AI is only ever used for the first step (turning free text into a service recommendation);
// everything past that — employee eligibility, slot availability, booking creation — goes
// through the existing, unchanged, transaction-safe AvailabilityService/BookingDraftService, so
// the model can never invent an employee, a time slot, or a booking.
[ApiController]
[Route("api/public/{slug}/chatbot")]
[AllowAnonymous]
public class PublicAiChatbotController : ControllerBase
{
    private const int MaxHistoryTurns = 10;
    private const int MaxSlotOptions = 6;
    private const int MaxDaysToScan = 21;

    private readonly GentleBookDbContext _db;
    private readonly IServiceFinderEngine _engine;
    private readonly IAiOrchestrator _orchestrator;
    private readonly AvailabilityService _availability;
    private readonly IBookingDraftService _bookingDraftService;

    public PublicAiChatbotController(
        GentleBookDbContext db,
        IServiceFinderEngine engine,
        IAiOrchestrator orchestrator,
        AvailabilityService availability,
        IBookingDraftService bookingDraftService)
    {
        _db = db;
        _engine = engine;
        _orchestrator = orchestrator;
        _availability = availability;
        _bookingDraftService = bookingDraftService;
    }

    // Same slug-resolution + subscription-access check as PublicAiFinderController — api/public/*
    // is exempt from TenantMiddleware's JWT-based gate since there's no token here.
    private async Task<(Guid tenantId, IActionResult? error)> ResolveTenantAsync(string slug, CancellationToken cancellationToken)
    {
        var tenant = await _db.Tenants
            .AsNoTracking()
            .Include(t => t.Subscription)
            .FirstOrDefaultAsync(t => t.Slug == slug.ToLowerInvariant() && t.IsActive, cancellationToken);

        if (tenant == null)
            return (Guid.Empty, NotFound(new { message = "Booking system not found." }));

        if (tenant.Subscription == null || !tenant.Subscription.IsAccessAllowed)
            return (Guid.Empty, StatusCode(402, new { message = "Trial expired or subscription inactive." }));

        return (tenant.Id, null);
    }

    private readonly record struct ChatbotAvailability(bool Enabled, bool IsAgencyGate);

    // Two independent switches must both be on: the tenant's own IsChatbotEnabled toggle (set in
    // Links/Design settings) AND the Agency plan gate — a tenant that downgrades away from Agency
    // loses the widget even if they'd left the toggle on, same as every other Agency-exclusive
    // feature in this codebase.
    private async Task<ChatbotAvailability> GetChatbotAvailabilityAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var settings = await _db.TenantSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);
        if (settings == null || !settings.IsChatbotEnabled)
            return new ChatbotAvailability(false, false);

        var tenantPlan = await _db.Subscriptions.AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .Select(s => (SubscriptionPlan?)s.Plan)
            .FirstOrDefaultAsync(cancellationToken);

        var isAgency = tenantPlan.HasValue && AgencyFeatureGate.IsAgency(tenantPlan.Value);
        return new ChatbotAvailability(isAgency, !isAgency);
    }

    [HttpGet("bootstrap")]
    public async Task<IActionResult> GetBootstrap(string slug, CancellationToken cancellationToken)
    {
        var (tenantId, error) = await ResolveTenantAsync(slug, cancellationToken);
        if (error != null) return error;

        var availability = await GetChatbotAvailabilityAsync(tenantId, cancellationToken);
        return Ok(new
        {
            enabled = availability.Enabled,
            aiDisclosure = "Du kommunizierst mit einem KI-gestützten Assistenten.",
        });
    }

    [HttpPost("messages")]
    [EnableRateLimiting("finder-limit")]
    public async Task<IActionResult> SendMessage(
        string slug,
        [FromBody] ChatbotMessageRequestDto request,
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = await ResolveTenantAsync(slug, cancellationToken);
        if (error != null) return error;

        var availability = await GetChatbotAvailabilityAsync(tenantId, cancellationToken);
        if (!availability.Enabled)
            return availability.IsAgencyGate
                ? StatusCode(402, new { message = "Der Chat-Assistent ist ab dem Tarif \"Agency\" verfügbar.", feature = "aiChatbot", upgrade = true, requiredPlan = "Agency" })
                : NotFound(new { message = "Der Chat-Assistent ist für diese Buchungsseite nicht aktiviert." });

        AiConversation? conversation = null;
        if (request.ConversationId.HasValue)
        {
            conversation = await _db.AiConversations
                .Include(c => c.Messages)
                .FirstOrDefaultAsync(c => c.Id == request.ConversationId.Value && c.TenantId == tenantId
                    && c.Channel == "PublicChatbot" && c.Status != AiConversationStatus.Completed && c.Status != AiConversationStatus.Abandoned, cancellationToken);
        }
        if (conversation == null)
        {
            conversation = new AiConversation { TenantId = tenantId, Channel = "PublicChatbot", Status = AiConversationStatus.Active };
            _db.AiConversations.Add(conversation);
        }

        // ── 1) Confirm an existing draft ────────────────────────────────
        if (request.ConfirmBooking)
        {
            if (!conversation.BookingDraftId.HasValue)
                return BadRequest(new { message = "Es liegt noch kein Buchungsentwurf vor." });

            try
            {
                var booking = await _bookingDraftService.ConfirmDraftAsync(tenantId, conversation.BookingDraftId.Value, true, cancellationToken);
                conversation.Status = AiConversationStatus.Completed;
                var reply = $"Fertig! Deine Buchung ist bestätigt — Buchungsnummer {booking.BookingNumber}.";
                _db.AiMessages.Add(new AiMessage { ConversationId = conversation.Id, Conversation = conversation, Role = "assistant", Content = reply });
                await _db.SaveChangesAsync(cancellationToken);

                return Ok(new ChatbotMessageResponseDto(
                    conversation.Id, conversation.Status, reply,
                    Array.Empty<ServiceRecommendationDto>(), Array.Empty<ChatbotEmployeeOptionDto>(), Array.Empty<ChatbotSlotOptionDto>(),
                    RequiresCustomerInfo: false, conversation.BookingDraftId, booking, RequiresHumanConsultation: false, UsedAiFallback: false));
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // ── 2) Service + slot + contact info known → create the draft ──
        if (request.SelectedServiceId.HasValue && !string.IsNullOrWhiteSpace(request.SelectedDate) && !string.IsNullOrWhiteSpace(request.SelectedTime) && request.Customer != null)
        {
            if (!request.SelectedEmployeeId.HasValue && !request.AnyEmployee)
                return BadRequest(new { message = "Bitte zuerst eine Person auswählen oder \"egal\" angeben." });

            try
            {
                var draft = await _bookingDraftService.CreateDraftAsync(tenantId, new CreateBookingDraftRequestDto(
                    request.SelectedServiceId.Value, request.SelectedEmployeeId, request.SelectedDate, request.SelectedTime, request.Customer, CustomerNotes: null), cancellationToken);

                conversation.BookingDraftId = draft.Id;
                conversation.Status = AiConversationStatus.DraftCreated;
                var reply = $"Alles bereit: {request.SelectedDate} um {request.SelectedTime}. Soll ich das jetzt für dich buchen?";
                _db.AiMessages.Add(new AiMessage { ConversationId = conversation.Id, Conversation = conversation, Role = "assistant", Content = reply });
                await _db.SaveChangesAsync(cancellationToken);

                return Ok(new ChatbotMessageResponseDto(
                    conversation.Id, conversation.Status, reply,
                    Array.Empty<ServiceRecommendationDto>(), Array.Empty<ChatbotEmployeeOptionDto>(), Array.Empty<ChatbotSlotOptionDto>(),
                    RequiresCustomerInfo: false, draft.Id, ConfirmedBooking: null, RequiresHumanConsultation: false, UsedAiFallback: false));
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // ── 3) Service + slot known, contact info still missing ────────
        if (request.SelectedServiceId.HasValue && !string.IsNullOrWhiteSpace(request.SelectedDate) && !string.IsNullOrWhiteSpace(request.SelectedTime))
        {
            conversation.Status = AiConversationStatus.AwaitingSlotChoice;
            var reply = "Fast geschafft — wie darf ich dich für die Buchung eintragen (Name, E-Mail oder Telefon)?";
            _db.AiMessages.Add(new AiMessage { ConversationId = conversation.Id, Conversation = conversation, Role = "assistant", Content = reply });
            await _db.SaveChangesAsync(cancellationToken);

            return Ok(new ChatbotMessageResponseDto(
                conversation.Id, conversation.Status, reply,
                Array.Empty<ServiceRecommendationDto>(), Array.Empty<ChatbotEmployeeOptionDto>(), Array.Empty<ChatbotSlotOptionDto>(),
                RequiresCustomerInfo: true, conversation.BookingDraftId, ConfirmedBooking: null, RequiresHumanConsultation: false, UsedAiFallback: false));
        }

        // ── 4) Service + employee choice known → offer real time slots ─
        if (request.SelectedServiceId.HasValue && (request.SelectedEmployeeId.HasValue || request.AnyEmployee))
        {
            var slots = await FindUpcomingSlotsAsync(tenantId, slug, request.SelectedServiceId.Value, request.SelectedEmployeeId, cancellationToken);
            conversation.Status = AiConversationStatus.AwaitingSlotChoice;
            var reply = slots.Count > 0
                ? "Hier sind die nächsten freien Termine — wähl einfach einen aus:"
                : "Ich finde gerade leider keinen freien Termin in den nächsten drei Wochen. Bitte kontaktiere das Studio direkt.";
            _db.AiMessages.Add(new AiMessage { ConversationId = conversation.Id, Conversation = conversation, Role = "assistant", Content = reply });
            await _db.SaveChangesAsync(cancellationToken);

            return Ok(new ChatbotMessageResponseDto(
                conversation.Id, conversation.Status, reply,
                Array.Empty<ServiceRecommendationDto>(), Array.Empty<ChatbotEmployeeOptionDto>(), slots,
                RequiresCustomerInfo: false, conversation.BookingDraftId, ConfirmedBooking: null,
                RequiresHumanConsultation: slots.Count == 0, UsedAiFallback: false));
        }

        // ── 5) Only a service is known → offer eligible employees ──────
        if (request.SelectedServiceId.HasValue)
        {
            var employees = await _db.ServiceEmployees
                .AsNoTracking()
                .Where(se => se.ServiceId == request.SelectedServiceId.Value && se.Employee.TenantId == tenantId && se.Employee.IsActive)
                .Select(se => new ChatbotEmployeeOptionDto(se.Employee.Id, se.Employee.Name, se.Employee.Role))
                .ToListAsync(cancellationToken);

            // 0 or 1 eligible employee — nothing to choose, go straight to slot options.
            if (employees.Count <= 1)
            {
                var slots = await FindUpcomingSlotsAsync(tenantId, slug, request.SelectedServiceId.Value, employees.Count == 1 ? employees[0].EmployeeId : null, cancellationToken);
                conversation.Status = AiConversationStatus.AwaitingSlotChoice;
                var reply = slots.Count > 0
                    ? "Hier sind die nächsten freien Termine — wähl einfach einen aus:"
                    : "Ich finde gerade leider keinen freien Termin in den nächsten drei Wochen. Bitte kontaktiere das Studio direkt.";
                _db.AiMessages.Add(new AiMessage { ConversationId = conversation.Id, Conversation = conversation, Role = "assistant", Content = reply });
                await _db.SaveChangesAsync(cancellationToken);

                return Ok(new ChatbotMessageResponseDto(
                    conversation.Id, conversation.Status, reply,
                    Array.Empty<ServiceRecommendationDto>(), Array.Empty<ChatbotEmployeeOptionDto>(), slots,
                    RequiresCustomerInfo: false, conversation.BookingDraftId, ConfirmedBooking: null,
                    RequiresHumanConsultation: slots.Count == 0, UsedAiFallback: false));
            }

            conversation.Status = AiConversationStatus.AwaitingEmployeeChoice;
            var employeeReply = "Möchtest du bei einer bestimmten Person buchen, oder ist es dir egal?";
            _db.AiMessages.Add(new AiMessage { ConversationId = conversation.Id, Conversation = conversation, Role = "assistant", Content = employeeReply });
            await _db.SaveChangesAsync(cancellationToken);

            return Ok(new ChatbotMessageResponseDto(
                conversation.Id, conversation.Status, employeeReply,
                Array.Empty<ServiceRecommendationDto>(), employees, Array.Empty<ChatbotSlotOptionDto>(),
                RequiresCustomerInfo: false, conversation.BookingDraftId, ConfirmedBooking: null, RequiresHumanConsultation: false, UsedAiFallback: false));
        }

        // ── 6) Nothing selected yet → free-text service recommendation ─
        if (string.IsNullOrWhiteSpace(request.Message))
            return BadRequest(new { message = "Bitte eine Nachricht angeben." });

        var history = conversation.Messages
            .OrderBy(m => m.CreatedAt)
            .TakeLast(MaxHistoryTurns)
            .Select(m => (Role: m.Role, Content: m.Content))
            .ToList();

        _db.AiMessages.Add(new AiMessage { ConversationId = conversation.Id, Conversation = conversation, Role = "user", Content = request.Message });

        var result = await _engine.FindServicesAsync(tenantId, new ServiceFinderAnswers(new Dictionary<string, System.Text.Json.JsonElement>()), cancellationToken);
        var turn = await _orchestrator.BuildChatbotTurnAsync(tenantId, conversation.Id, history, result, request.Message, cancellationToken);

        _db.AiMessages.Add(new AiMessage { ConversationId = conversation.Id, Conversation = conversation, Role = "assistant", Content = turn.Reply });
        await _db.SaveChangesAsync(cancellationToken);

        var recommended = result.Recommendations
            .Where(r => turn.RecommendedServiceIds.Contains(r.ServiceId))
            .Select(r => new ServiceRecommendationDto(r.ServiceId, r.ServiceName, r.Score, r.Price, r.Currency, r.DurationMinutes, r.SuggestedEmployeeIds))
            .ToList();

        return Ok(new ChatbotMessageResponseDto(
            conversation.Id, conversation.Status, turn.Reply, recommended,
            Array.Empty<ChatbotEmployeeOptionDto>(), Array.Empty<ChatbotSlotOptionDto>(),
            RequiresCustomerInfo: false, conversation.BookingDraftId, ConfirmedBooking: null,
            turn.RequiresHumanConsultation, turn.UsedAiFallback));
    }

    // Scans forward day-by-day (capped by the tenant's own MaxAdvanceBookingDays, and by
    // MaxDaysToScan as a hard ceiling for chat responsiveness) using the same, unchanged
    // AvailabilityService the click-wizard already uses — real slots only, the model never sees
    // or influences this list before it's built.
    private async Task<List<ChatbotSlotOptionDto>> FindUpcomingSlotsAsync(
        Guid tenantId, string slug, Guid serviceId, Guid? employeeId, CancellationToken cancellationToken)
    {
        var maxAdvanceDays = await _db.TenantSettings.AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .Select(s => (int?)s.MaxAdvanceBookingDays)
            .FirstOrDefaultAsync(cancellationToken) ?? MaxDaysToScan;
        var daysToScan = Math.Min(MaxDaysToScan, Math.Max(1, maxAdvanceDays));

        var results = new List<ChatbotSlotOptionDto>();
        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);
        var dayNames = new[] { "So", "Mo", "Di", "Mi", "Do", "Fr", "Sa" };

        for (var i = 0; i < daysToScan && results.Count < MaxSlotOptions; i++)
        {
            var date = today.AddDays(i);
            AvailabilityResponseDto response;
            try
            {
                response = await _availability.GetAvailableTimeSlotsAsync(serviceId, date, employeeId, waitlistToken: null, tenantSlug: slug);
            }
            catch (ArgumentException)
            {
                break; // service/tenant resolution failed — nothing more to scan
            }

            foreach (var slot in response.AvailableSlots.Where(s => s.IsAvailable))
            {
                if (results.Count >= MaxSlotOptions) break;

                // AvailabilityService itself doesn't filter out already-passed times for today
                // (that check lives in BookingService.CreateBookingAsync, further down the
                // pipeline) — offering one here would let the chat present a slot that then
                // fails at draft-confirm time. Skip it up front instead.
                if (i == 0 && TimeOnly.TryParse(slot.StartTime, out var startTime) && date.ToDateTime(startTime) <= now)
                    continue;

                var label = $"{dayNames[(int)date.DayOfWeek]} {date:dd.MM.} {slot.StartTime}";
                results.Add(new ChatbotSlotOptionDto(date.ToString("yyyy-MM-dd"), slot.StartTime, label));
            }
        }

        return results;
    }
}
