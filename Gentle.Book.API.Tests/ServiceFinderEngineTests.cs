using System.Text.Json;
using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services.AI;

namespace Gentle.Book.API.Tests;

public class ServiceFinderEngineTests
{
    [Fact]
    public async Task FindServicesAsync_RecognizesMissingRequiredQuestions_And_AppliesRules()
    {
        using var db = TestDbContextFactory.Create();

        var tenant = new Tenant { Name = "A", Slug = "a", IsActive = true };
        db.Tenants.Add(tenant);

        var profile = new IndustryProfile { Key = "beauty_aesthetics", Name = "Beauty", IsActive = true };
        db.IndustryProfiles.Add(profile);

        db.TenantIndustrySettings.Add(new TenantIndustrySetting
        {
            TenantId = tenant.Id,
            PrimaryIndustryProfileId = profile.Id,
            IsFinderEnabled = true
        });

        var category = new ServiceCategory
        {
            TenantId = tenant.Id,
            Name = "Face",
            IsActive = true
        };
        db.ServiceCategories.Add(category);

        var consultation = new Service
        {
            TenantId = tenant.Id,
            CategoryId = category.Id,
            Name = "Beratung",
            DurationMinutes = 30,
            Price = 30,
            Currency = "EUR",
            IsActive = true
        };

        var treatment = new Service
        {
            TenantId = tenant.Id,
            CategoryId = category.Id,
            Name = "Microneedling",
            DurationMinutes = 60,
            Price = 120,
            Currency = "EUR",
            IsActive = true
        };

        db.Services.AddRange(consultation, treatment);

        db.ServiceFinderQuestions.AddRange(
            new ServiceFinderQuestion
            {
                TenantId = tenant.Id,
                QuestionKey = "goal",
                QuestionText = "Was ist dein Ziel?",
                AnswerType = ServiceFinderAnswerType.SingleChoice,
                IsRequired = true,
                IsActive = true,
                DisplayOrder = 1
            },
            new ServiceFinderQuestion
            {
                TenantId = tenant.Id,
                QuestionKey = "isFirstTime",
                QuestionText = "Erstbehandlung?",
                AnswerType = ServiceFinderAnswerType.YesNo,
                IsRequired = true,
                IsActive = true,
                DisplayOrder = 2
            });

        db.ServiceFinderRules.AddRange(
            new ServiceFinderRule
            {
                TenantId = tenant.Id,
                ServiceId = treatment.Id,
                RuleType = ServiceFinderRuleType.ExcludeService,
                ConditionJson = "{\"questionKey\":\"isFirstTime\",\"operator\":\"equals\",\"value\":true}",
                ResultJson = "{}",
                Priority = 200,
                IsActive = true,
                ApprovalStatus = ApprovalStatus.Approved
            },
            new ServiceFinderRule
            {
                TenantId = tenant.Id,
                ServiceId = consultation.Id,
                RuleType = ServiceFinderRuleType.RecommendService,
                ConditionJson = "{\"questionKey\":\"isFirstTime\",\"operator\":\"equals\",\"value\":true}",
                ResultJson = "{\"score\":80}",
                Priority = 150,
                IsActive = true,
                ApprovalStatus = ApprovalStatus.Approved
            });

        await db.SaveChangesAsync();

        var engine = new ServiceFinderEngine(db);
        var answers = new Dictionary<string, JsonElement>
        {
            ["isFirstTime"] = JsonSerializer.SerializeToElement(true)
        };

        var result = await engine.FindServicesAsync(tenant.Id, new ServiceFinderAnswers(answers), CancellationToken.None);

        Assert.Contains(result.MissingQuestions, q => q.QuestionKey == "goal");
        Assert.DoesNotContain(result.Recommendations, r => r.ServiceId == treatment.Id);
        Assert.Contains(result.Recommendations, r => r.ServiceId == consultation.Id);
    }

    [Fact]
    public async Task FindServicesAsync_IsTenantIsolated()
    {
        using var db = TestDbContextFactory.Create();

        var tenantA = new Tenant { Name = "A", Slug = "a", IsActive = true };
        var tenantB = new Tenant { Name = "B", Slug = "b", IsActive = true };
        db.Tenants.AddRange(tenantA, tenantB);

        var profile = new IndustryProfile { Key = "barbershop_hairdresser", Name = "Barber", IsActive = true };
        db.IndustryProfiles.Add(profile);

        db.TenantIndustrySettings.AddRange(
            new TenantIndustrySetting { TenantId = tenantA.Id, PrimaryIndustryProfileId = profile.Id, IsFinderEnabled = true },
            new TenantIndustrySetting { TenantId = tenantB.Id, PrimaryIndustryProfileId = profile.Id, IsFinderEnabled = true });

        var catA = new ServiceCategory { TenantId = tenantA.Id, Name = "Hair", IsActive = true };
        var catB = new ServiceCategory { TenantId = tenantB.Id, Name = "Hair", IsActive = true };
        db.ServiceCategories.AddRange(catA, catB);

        var serviceA = new Service { TenantId = tenantA.Id, CategoryId = catA.Id, Name = "A-Service", DurationMinutes = 30, Price = 20, Currency = "EUR", IsActive = true };
        var serviceB = new Service { TenantId = tenantB.Id, CategoryId = catB.Id, Name = "B-Service", DurationMinutes = 30, Price = 20, Currency = "EUR", IsActive = true };
        db.Services.AddRange(serviceA, serviceB);

        db.ServiceFinderRules.AddRange(
            new ServiceFinderRule
            {
                TenantId = tenantA.Id,
                ServiceId = serviceA.Id,
                RuleType = ServiceFinderRuleType.RecommendService,
                ConditionJson = "{}",
                ResultJson = "{\"score\":50}",
                Priority = 100,
                IsActive = true,
                ApprovalStatus = ApprovalStatus.Approved
            },
            new ServiceFinderRule
            {
                TenantId = tenantB.Id,
                ServiceId = serviceB.Id,
                RuleType = ServiceFinderRuleType.RecommendService,
                ConditionJson = "{}",
                ResultJson = "{\"score\":50}",
                Priority = 100,
                IsActive = true,
                ApprovalStatus = ApprovalStatus.Approved
            });

        await db.SaveChangesAsync();

        var engine = new ServiceFinderEngine(db);
        var resultA = await engine.FindServicesAsync(tenantA.Id, new ServiceFinderAnswers(new Dictionary<string, JsonElement>()), CancellationToken.None);

        Assert.Contains(resultA.Recommendations, r => r.ServiceId == serviceA.Id);
        Assert.DoesNotContain(resultA.Recommendations, r => r.ServiceId == serviceB.Id);
    }
}
