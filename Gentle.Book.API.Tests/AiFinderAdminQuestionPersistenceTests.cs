using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.DTOs;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Gentle.Book.API.Tests;

// Found during live pre-launch testing: toggling an existing Finder question to isActive:false
// via UpsertQuestions reported success, but reappeared as active after a reload. Covers whether
// UpsertQuestions -> GetQuestions actually round-trips an IsActive flip for an existing question
// matched by Id, in isolation.
public class AiFinderAdminQuestionPersistenceTests
{
    [Fact]
    public async Task UpsertQuestions_ToggleExistingQuestionToInactive_PersistsAndIsReflectedOnReload()
    {
        using var db = TestDbContextFactory.Create();
        var tenant = new Tenant { Name = "Salon Finder-Test", Slug = "salon-finder-test-" + Guid.NewGuid(), IsActive = true };
        db.Tenants.Add(tenant);
        var question = new ServiceFinderQuestion
        {
            TenantId = tenant.Id,
            QuestionKey = "GB-FINDER-08",
            QuestionText = "Test question",
            AnswerType = ServiceFinderAnswerType.SingleChoice,
            IsRequired = true,
            DisplayOrder = 0,
            IsActive = true,
        };
        db.ServiceFinderQuestions.Add(question);
        db.SaveChanges();

        var controller = AiFinderAdminControllerFactory.Create(db, tenant.Id, "TenantAdmin");

        var upsertResult = await controller.UpsertQuestions(new UpsertFinderQuestionsRequestDto(
        [
            new FinderQuestionUpsertItemDto(
                question.Id, null, question.QuestionKey, question.QuestionText,
                question.AnswerType, question.IsRequired, question.DisplayOrder, question.ConfigJson,
                IsActive: false)
        ]), default);

        Assert.IsType<OkObjectResult>(upsertResult);

        var getResult = await controller.GetQuestions(default);
        var ok = Assert.IsType<OkObjectResult>(getResult);
        var questions = Assert.IsAssignableFrom<IEnumerable<FinderQuestionDto>>(ok.Value);
        var reloaded = Assert.Single(questions, q => q.Id == question.Id);
        Assert.False(reloaded.IsActive);
    }

    [Fact]
    public async Task UpsertQuestions_MissingId_MatchesExistingRowByQuestionKeyInsteadOfDuplicating()
    {
        // If the admin's hand-edited JSON ever drops the id for an existing question (plausible
        // given this is a raw-JSON textarea, not a structured form), UpsertQuestions falls back to
        // matching the existing row by (TenantId, QuestionKey) rather than creating a duplicate —
        // a duplicate row would silently corrupt reads elsewhere (see
        // ServiceFinderEngineTests and the AdminNav-style "edit doesn't seem to stick" symptom).
        using var db = TestDbContextFactory.Create();
        var tenant = new Tenant { Name = "Salon Finder-Test-2", Slug = "salon-finder-test-2-" + Guid.NewGuid(), IsActive = true };
        db.Tenants.Add(tenant);
        var question = new ServiceFinderQuestion
        {
            TenantId = tenant.Id,
            QuestionKey = "GB-FINDER-08",
            QuestionText = "Test question",
            AnswerType = ServiceFinderAnswerType.SingleChoice,
            IsRequired = true,
            DisplayOrder = 0,
            IsActive = true,
        };
        db.ServiceFinderQuestions.Add(question);
        db.SaveChanges();

        var controller = AiFinderAdminControllerFactory.Create(db, tenant.Id, "TenantAdmin");

        var upsertResult = await controller.UpsertQuestions(new UpsertFinderQuestionsRequestDto(
        [
            new FinderQuestionUpsertItemDto(
                Id: null, null, question.QuestionKey, question.QuestionText,
                question.AnswerType, question.IsRequired, question.DisplayOrder, question.ConfigJson,
                IsActive: false)
        ]), default);

        Assert.IsType<OkObjectResult>(upsertResult);
        Assert.Equal(1, db.ServiceFinderQuestions.Count(q => q.TenantId == tenant.Id));
        var reloaded = db.ServiceFinderQuestions.Single(q => q.TenantId == tenant.Id);
        Assert.Equal(question.Id, reloaded.Id);
        Assert.False(reloaded.IsActive);
    }
}
