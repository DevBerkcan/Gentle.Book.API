using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Gentle.Book.API.Tests;

// Covers Phase 4: employee/slot picking through to a confirmed booking, entirely via
// PublicAiChatbotController.SendMessage's structured fields (no free text involved past the
// initial service pick). The AI is never on the critical path here — every step reads real data
// from ServiceEmployees/AvailabilityService and creates the booking through the same, unchanged
// BookingDraftService the click-wizard already uses.
public class PublicAiChatbotBookingFlowTests
{
    private static (Tenant tenant, Service service) SeedTenantWithService(
        GentleBook.Api.Data.GentleBookDbContext db, int employeeCount, bool chatbotEnabled = true, SubscriptionPlan plan = SubscriptionPlan.Agency)
    {
        var tenant = new Tenant { Name = "Salon Chatflow", Slug = "salon-chatflow-" + Guid.NewGuid(), IsActive = true };
        db.Tenants.Add(tenant);
        db.Subscriptions.Add(new Subscription { TenantId = tenant.Id, Tenant = tenant, Plan = plan, Status = SubscriptionStatus.Active });
        db.TenantSettings.Add(new TenantSettings { TenantId = tenant.Id, CompanyName = "Salon Chatflow", IsChatbotEnabled = chatbotEnabled, BookingIntervalMinutes = 30, MaxAdvanceBookingDays = 60 });

        // Every weekday open 09:00–18:00 so the test is deterministic regardless of when it runs.
        foreach (System.DayOfWeek day in Enum.GetValues(typeof(System.DayOfWeek)))
        {
            db.BusinessHours.Add(new BusinessHours { TenantId = tenant.Id, DayOfWeek = day, OpenTime = new TimeOnly(9, 0), CloseTime = new TimeOnly(18, 0), IsOpen = true });
        }

        var category = new ServiceCategory { TenantId = tenant.Id, Name = "Haarschnitt" };
        db.ServiceCategories.Add(category);
        var service = new Service { TenantId = tenant.Id, CategoryId = category.Id, Name = "Waschen, Schneiden, Föhnen", DurationMinutes = 30, Price = 40m, IsActive = true };
        db.Services.Add(service);

        for (var i = 0; i < employeeCount; i++)
        {
            var employee = new Employee { TenantId = tenant.Id, Name = $"Mitarbeiter {i}", Role = "Friseur", IsActive = true };
            db.Employees.Add(employee);
            db.ServiceEmployees.Add(new ServiceEmployee { ServiceId = service.Id, EmployeeId = employee.Id });
        }

        db.SaveChanges();
        return (tenant, service);
    }

    private static ChatbotMessageRequestDto Msg(
        Guid? conversationId = null, string? message = null, Guid? serviceId = null, Guid? employeeId = null,
        bool anyEmployee = false, string? date = null, string? time = null, CustomerInfoDto? customer = null, bool confirm = false) =>
        new(conversationId, message, serviceId, employeeId, anyEmployee, date, time, customer, confirm);

    [Fact]
    public async Task SelectService_ZeroEligibleEmployees_SkipsChoiceButFindsNoSlots()
    {
        // No employee is assigned to the service at all, so there is genuinely nobody who could
        // perform it — AvailabilityService correctly reports zero availability rather than the
        // chatbot inventing a booking with no one behind it.
        using var db = TestDbContextFactory.Create();
        var (tenant, service) = SeedTenantWithService(db, employeeCount: 0);
        var controller = PublicAiChatbotControllerFactory.Create(db);

        var result = await controller.SendMessage(tenant.Slug, Msg(serviceId: service.Id), CancellationToken.None);

        var response = Assert.IsType<ChatbotMessageResponseDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Empty(response.EmployeeOptions);
        Assert.Empty(response.SlotOptions);
        Assert.True(response.RequiresHumanConsultation);
    }

    [Fact]
    public async Task SelectService_OneEligibleEmployee_SkipsChoiceGoesStraightToSlots()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, service) = SeedTenantWithService(db, employeeCount: 1);
        var controller = PublicAiChatbotControllerFactory.Create(db);

        var result = await controller.SendMessage(tenant.Slug, Msg(serviceId: service.Id), CancellationToken.None);

        var response = Assert.IsType<ChatbotMessageResponseDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Empty(response.EmployeeOptions);
        Assert.NotEmpty(response.SlotOptions);
    }

    [Fact]
    public async Task SelectService_MultipleEligibleEmployees_ReturnsEmployeeOptions()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, service) = SeedTenantWithService(db, employeeCount: 2);
        var controller = PublicAiChatbotControllerFactory.Create(db);

        var result = await controller.SendMessage(tenant.Slug, Msg(serviceId: service.Id), CancellationToken.None);

        var response = Assert.IsType<ChatbotMessageResponseDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(2, response.EmployeeOptions.Count);
        Assert.Empty(response.SlotOptions);
        Assert.Equal("AwaitingEmployeeChoice", response.Status);
    }

    [Fact]
    public async Task FullFlow_ServiceThenAnyEmployeeThenSlotThenCustomer_CreatesDraft()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, service) = SeedTenantWithService(db, employeeCount: 2);
        var controller = PublicAiChatbotControllerFactory.Create(db);

        var step1 = await controller.SendMessage(tenant.Slug, Msg(serviceId: service.Id), CancellationToken.None);
        var r1 = Assert.IsType<ChatbotMessageResponseDto>(Assert.IsType<OkObjectResult>(step1).Value);

        var step2 = await controller.SendMessage(tenant.Slug, Msg(r1.ConversationId, serviceId: service.Id, anyEmployee: true), CancellationToken.None);
        var r2 = Assert.IsType<ChatbotMessageResponseDto>(Assert.IsType<OkObjectResult>(step2).Value);
        Assert.NotEmpty(r2.SlotOptions);
        var chosenSlot = r2.SlotOptions[0];

        var step3 = await controller.SendMessage(tenant.Slug, Msg(r1.ConversationId, serviceId: service.Id, anyEmployee: true, date: chosenSlot.Date, time: chosenSlot.Time), CancellationToken.None);
        var r3 = Assert.IsType<ChatbotMessageResponseDto>(Assert.IsType<OkObjectResult>(step3).Value);
        Assert.True(r3.RequiresCustomerInfo);

        var customer = new CustomerInfoDto("Anna", "Kunde", "anna@example.com", "+491234567");
        var step4 = await controller.SendMessage(tenant.Slug, Msg(r1.ConversationId, serviceId: service.Id, anyEmployee: true, date: chosenSlot.Date, time: chosenSlot.Time, customer: customer), CancellationToken.None);
        var r4 = Assert.IsType<ChatbotMessageResponseDto>(Assert.IsType<OkObjectResult>(step4).Value);
        Assert.NotNull(r4.BookingDraftId);
        Assert.Equal("DraftCreated", r4.Status);

        var draft = await db.ServiceFinderBookingDrafts.SingleAsync(d => d.Id == r4.BookingDraftId);
        Assert.Equal(BookingDraftStatus.Pending, draft.Status);
        var conversation = await db.AiConversations.SingleAsync(c => c.Id == r1.ConversationId);
        Assert.Equal(r4.BookingDraftId, conversation.BookingDraftId);
    }

    // Confirming a valid Pending draft reaches BookingDraftService.ConfirmDraftAsync →
    // BookingService.CreateBookingAsync, which opens a SERIALIZABLE transaction immediately (for
    // slot-availability locking) — EF Core's InMemory provider doesn't support relational
    // transactions, so a full successful booking can't be driven through this path in a unit
    // test (same documented limitation as BookingMonthlyLimitTests). Real SQL Server handles it
    // fine; this was verified against production data during the live pre-launch test instead.
    // What IS fully testable here, and covered below: SendMessage correctly rejects a confirm
    // attempt with no draft on the conversation (ConfirmBooking_WithoutADraft_IsRejected), and
    // the draft itself is created correctly with all the right data
    // (FullFlow_ServiceThenAnyEmployeeThenSlotThenCustomer_CreatesDraft) — everything up to the
    // point where BookingService's own transaction takes over.

    [Fact]
    public async Task ConfirmBooking_WithoutADraft_IsRejected()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, _) = SeedTenantWithService(db, employeeCount: 1);
        var controller = PublicAiChatbotControllerFactory.Create(db);

        var start = await controller.SendMessage(tenant.Slug, Msg(message: "Hallo"), CancellationToken.None);
        var r1 = Assert.IsType<ChatbotMessageResponseDto>(Assert.IsType<OkObjectResult>(start).Value);

        var result = await controller.SendMessage(tenant.Slug, Msg(r1.ConversationId, confirm: true), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task SelectServiceAndSlot_NeitherEmployeeNorAnyChosen_IsRejected()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, service) = SeedTenantWithService(db, employeeCount: 1);
        var controller = PublicAiChatbotControllerFactory.Create(db);
        var customer = new CustomerInfoDto("Carla", "Customer", "carla@example.com", null);

        var result = await controller.SendMessage(
            tenant.Slug,
            Msg(serviceId: service.Id, anyEmployee: false, date: DateTime.UtcNow.ToString("yyyy-MM-dd"), time: "10:00", customer: customer),
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }
}
