using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Gentle.Book.API.Tests;

// Covers the new Agency-exclusive chat booking assistant widget (Phase 3, first slice: free-text
// service recommendation only — no employee/slot picking yet). Same "api/public/* enforces its
// own subscription/plan gate" concern as PublicAiFinderControllerTests, plus the tenant's own
// TenantSettings.IsChatbotEnabled toggle (Links/Design settings) and conversation persistence.
public class PublicAiChatbotControllerTests
{
    private static Tenant SeedTenant(
        GentleBook.Api.Data.GentleBookDbContext db,
        SubscriptionPlan plan,
        SubscriptionStatus status,
        bool? isChatbotEnabled,
        int? maxMessagesPerConversation = null,
        int? maxMessagesPerVisitorPerDay = null)
    {
        var tenant = new Tenant { Name = "Barber Wagner", Slug = "barber-wagner", IsActive = true };
        var subscription = new Subscription { TenantId = tenant.Id, Tenant = tenant, Plan = plan, Status = status };
        tenant.Subscription = subscription;

        db.Tenants.Add(tenant);
        db.Subscriptions.Add(subscription);

        if (isChatbotEnabled.HasValue)
        {
            var settings = new TenantSettings
            {
                TenantId = tenant.Id,
                CompanyName = "Barber Wagner",
                IsChatbotEnabled = isChatbotEnabled.Value,
            };
            if (maxMessagesPerConversation.HasValue)
                settings.ChatbotMaxMessagesPerConversation = maxMessagesPerConversation.Value;
            if (maxMessagesPerVisitorPerDay.HasValue)
                settings.ChatbotMaxMessagesPerVisitorPerDay = maxMessagesPerVisitorPerDay.Value;
            db.TenantSettings.Add(settings);
        }

        db.SaveChanges();
        return tenant;
    }

    private static ChatbotMessageRequestDto Message(string text, Guid? conversationId = null) => new(
        conversationId, text, SelectedServiceId: null, SelectedEmployeeId: null, AnyEmployee: false,
        SelectedDate: null, SelectedTime: null, Customer: null, ConfirmBooking: false);

    [Fact]
    public async Task SendMessage_SubscriptionInactive_Returns402()
    {
        using var db = TestDbContextFactory.Create();
        SeedTenant(db, SubscriptionPlan.Agency, SubscriptionStatus.Cancelled, isChatbotEnabled: true);
        var controller = PublicAiChatbotControllerFactory.Create(db);

        var result = await controller.SendMessage("barber-wagner", Message("Hallo"), CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(402, objectResult.StatusCode);
    }

    [Fact]
    public async Task SendMessage_ChatbotDisabled_Returns404()
    {
        using var db = TestDbContextFactory.Create();
        SeedTenant(db, SubscriptionPlan.Agency, SubscriptionStatus.Active, isChatbotEnabled: false);
        var controller = PublicAiChatbotControllerFactory.Create(db);

        var result = await controller.SendMessage("barber-wagner", Message("Hallo"), CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task SendMessage_NoTenantSettingsAtAll_Returns404()
    {
        using var db = TestDbContextFactory.Create();
        SeedTenant(db, SubscriptionPlan.Agency, SubscriptionStatus.Active, isChatbotEnabled: null);
        var controller = PublicAiChatbotControllerFactory.Create(db);

        var result = await controller.SendMessage("barber-wagner", Message("Hallo"), CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task SendMessage_NonAgencyPlanEvenWithToggleOn_Returns402WithUpgradeFlag()
    {
        // Defense in depth: the toggle itself shouldn't be reachable on a lower plan, but a
        // downgrade after enabling it must still be caught here, same as the Finder's gate.
        using var db = TestDbContextFactory.Create();
        SeedTenant(db, SubscriptionPlan.Professional, SubscriptionStatus.Active, isChatbotEnabled: true);
        var controller = PublicAiChatbotControllerFactory.Create(db);

        var result = await controller.SendMessage("barber-wagner", Message("Hallo"), CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(402, objectResult.StatusCode);
        var json = System.Text.Json.JsonSerializer.Serialize(objectResult.Value);
        Assert.Contains("upgrade", json);
    }

    [Fact]
    public async Task SendMessage_EmptyMessage_Returns400()
    {
        using var db = TestDbContextFactory.Create();
        SeedTenant(db, SubscriptionPlan.Agency, SubscriptionStatus.Active, isChatbotEnabled: true);
        var controller = PublicAiChatbotControllerFactory.Create(db);

        var result = await controller.SendMessage("barber-wagner", Message("   "), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task SendMessage_AgencyPlanEnabled_CreatesConversationAndPersistsMessages()
    {
        using var db = TestDbContextFactory.Create();
        var tenant = SeedTenant(db, SubscriptionPlan.Agency, SubscriptionStatus.Active, isChatbotEnabled: true);
        var controller = PublicAiChatbotControllerFactory.Create(db);

        var result = await controller.SendMessage("barber-wagner", Message("Ich habe viele Haare am Rücken."), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ChatbotMessageResponseDto>(ok.Value);
        Assert.NotEqual(Guid.Empty, response.ConversationId);
        Assert.True(response.UsedAiFallback); // NullAiProviderAdapter always falls back

        var conversation = await db.AiConversations.Include(c => c.Messages).SingleAsync(c => c.Id == response.ConversationId);
        Assert.Equal(tenant.Id, conversation.TenantId);
        Assert.Equal("PublicChatbot", conversation.Channel);
        Assert.Equal(2, conversation.Messages.Count); // user + assistant
        Assert.Contains(conversation.Messages, m => m.Role == "user" && m.Content.Contains("Rücken"));
        Assert.Contains(conversation.Messages, m => m.Role == "assistant");
    }

    [Fact]
    public async Task SendMessage_ExistingConversationId_AppendsToSameConversation()
    {
        using var db = TestDbContextFactory.Create();
        SeedTenant(db, SubscriptionPlan.Agency, SubscriptionStatus.Active, isChatbotEnabled: true);
        var controller = PublicAiChatbotControllerFactory.Create(db);

        var first = await controller.SendMessage("barber-wagner", Message("Ich brauche einen Haarschnitt."), CancellationToken.None);
        var firstResponse = Assert.IsType<ChatbotMessageResponseDto>(Assert.IsType<OkObjectResult>(first).Value);

        var second = await controller.SendMessage("barber-wagner", Message("Am liebsten nachmittags.", firstResponse.ConversationId), CancellationToken.None);
        var secondResponse = Assert.IsType<ChatbotMessageResponseDto>(Assert.IsType<OkObjectResult>(second).Value);

        Assert.Equal(firstResponse.ConversationId, secondResponse.ConversationId);
        var conversation = await db.AiConversations.Include(c => c.Messages).SingleAsync(c => c.Id == firstResponse.ConversationId);
        Assert.Equal(4, conversation.Messages.Count); // 2 user + 2 assistant turns
    }

    [Fact]
    public async Task SendMessage_MessageTooLong_Returns400()
    {
        using var db = TestDbContextFactory.Create();
        SeedTenant(db, SubscriptionPlan.Agency, SubscriptionStatus.Active, isChatbotEnabled: true);
        var controller = PublicAiChatbotControllerFactory.Create(db);

        var tooLong = new string('a', 1001);
        var result = await controller.SendMessage("barber-wagner", Message(tooLong), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task SendMessage_AdminConfiguredLimitReached_DeclinesWithoutAdditionalUserMessage()
    {
        // Admin-tunable via TenantSettings.ChatbotMaxMessagesPerConversation (Links/Design
        // settings) — set to 1 here so the second free-text turn is the one that trips it.
        using var db = TestDbContextFactory.Create();
        SeedTenant(db, SubscriptionPlan.Agency, SubscriptionStatus.Active, isChatbotEnabled: true, maxMessagesPerConversation: 1);
        var controller = PublicAiChatbotControllerFactory.Create(db);

        var first = await controller.SendMessage("barber-wagner", Message("Ich brauche einen Haarschnitt."), CancellationToken.None);
        var firstResponse = Assert.IsType<ChatbotMessageResponseDto>(Assert.IsType<OkObjectResult>(first).Value);

        var second = await controller.SendMessage("barber-wagner", Message("Und noch eine Bartrasur bitte.", firstResponse.ConversationId), CancellationToken.None);
        var secondResponse = Assert.IsType<ChatbotMessageResponseDto>(Assert.IsType<OkObjectResult>(second).Value);

        Assert.True(secondResponse.RequiresHumanConsultation);
        Assert.Contains("maximale Anzahl", secondResponse.Reply);

        var conversation = await db.AiConversations.Include(c => c.Messages).SingleAsync(c => c.Id == firstResponse.ConversationId);
        Assert.Single(conversation.Messages, m => m.Role == "user"); // second turn never became a tracked user message
    }

    [Fact]
    public async Task SendMessage_VisitorDailyLimitReached_BlocksEvenAcrossNewConversation()
    {
        // The whole point of the visitor-level cap: reloading the widget starts a brand-new
        // conversation (fresh per-conversation counter), but the same visitor (same IP) must
        // still be blocked once their daily total is reached — otherwise the per-conversation
        // limit alone would be trivially bypassable by refreshing the page.
        using var db = TestDbContextFactory.Create();
        var tenant = SeedTenant(db, SubscriptionPlan.Agency, SubscriptionStatus.Active, isChatbotEnabled: true,
            maxMessagesPerConversation: 100, maxMessagesPerVisitorPerDay: 1);
        var controller = PublicAiChatbotControllerFactory.Create(db, ipAddress: "198.51.100.7");

        var first = await controller.SendMessage("barber-wagner", Message("Ich brauche einen Haarschnitt."), CancellationToken.None);
        Assert.IsType<OkObjectResult>(first);

        // Simulate a page reload: no ConversationId sent, so a new AiConversation is created.
        var second = await controller.SendMessage("barber-wagner", Message("Neue Frage nach Reload."), CancellationToken.None);
        var secondResponse = Assert.IsType<ChatbotMessageResponseDto>(Assert.IsType<OkObjectResult>(second).Value);

        Assert.True(secondResponse.RequiresHumanConsultation);
        Assert.Contains("heutige", secondResponse.Reply);

        var conversations = await db.AiConversations.Include(c => c.Messages).Where(c => c.TenantId == tenant.Id).ToListAsync();
        Assert.Equal(2, conversations.Count); // two distinct conversations, as if reloaded
        Assert.Single(conversations.SelectMany(c => c.Messages).Where(m => m.Role == "user")); // only the first turn ever became a tracked user message
    }

    [Fact]
    public async Task SendMessage_DifferentVisitors_EachGetOwnDailyQuota()
    {
        using var db = TestDbContextFactory.Create();
        SeedTenant(db, SubscriptionPlan.Agency, SubscriptionStatus.Active, isChatbotEnabled: true,
            maxMessagesPerConversation: 100, maxMessagesPerVisitorPerDay: 1);

        var visitorA = PublicAiChatbotControllerFactory.Create(db, ipAddress: "198.51.100.1");
        var visitorB = PublicAiChatbotControllerFactory.Create(db, ipAddress: "198.51.100.2");

        var resultA = await visitorA.SendMessage("barber-wagner", Message("Frage von Besucher A."), CancellationToken.None);
        var resultB = await visitorB.SendMessage("barber-wagner", Message("Frage von Besucher B."), CancellationToken.None);

        var responseA = Assert.IsType<ChatbotMessageResponseDto>(Assert.IsType<OkObjectResult>(resultA).Value);
        var responseB = Assert.IsType<ChatbotMessageResponseDto>(Assert.IsType<OkObjectResult>(resultB).Value);

        Assert.False(responseA.RequiresHumanConsultation);
        Assert.False(responseB.RequiresHumanConsultation);
    }

    [Fact]
    public async Task GetBootstrap_TenantNotFound_Returns404()
    {
        using var db = TestDbContextFactory.Create();
        var controller = PublicAiChatbotControllerFactory.Create(db);

        var result = await controller.GetBootstrap("does-not-exist", CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task GetBootstrap_Disabled_ReturnsEnabledFalse()
    {
        using var db = TestDbContextFactory.Create();
        SeedTenant(db, SubscriptionPlan.Professional, SubscriptionStatus.Active, isChatbotEnabled: null);
        var controller = PublicAiChatbotControllerFactory.Create(db);

        var result = await controller.GetBootstrap("barber-wagner", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"enabled\":false", json);
    }
}
