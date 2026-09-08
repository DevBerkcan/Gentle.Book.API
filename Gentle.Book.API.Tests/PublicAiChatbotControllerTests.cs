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
        bool? isChatbotEnabled)
    {
        var tenant = new Tenant { Name = "Barber Wagner", Slug = "barber-wagner", IsActive = true };
        var subscription = new Subscription { TenantId = tenant.Id, Tenant = tenant, Plan = plan, Status = status };
        tenant.Subscription = subscription;

        db.Tenants.Add(tenant);
        db.Subscriptions.Add(subscription);

        if (isChatbotEnabled.HasValue)
        {
            db.TenantSettings.Add(new TenantSettings
            {
                TenantId = tenant.Id,
                CompanyName = "Barber Wagner",
                IsChatbotEnabled = isChatbotEnabled.Value,
            });
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
