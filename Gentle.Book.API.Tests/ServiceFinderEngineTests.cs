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

        var employee = new Employee { TenantId = tenant.Id, Name = "Mara Mitarbeiterin", IsActive = true };
        db.Employees.Add(employee);
        db.ServiceEmployees.AddRange(
            new ServiceEmployee { ServiceId = consultation.Id, EmployeeId = employee.Id },
            new ServiceEmployee { ServiceId = treatment.Id, EmployeeId = employee.Id });

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

        var employeeA = new Employee { TenantId = tenantA.Id, Name = "Mara A", IsActive = true };
        var employeeB = new Employee { TenantId = tenantB.Id, Name = "Mara B", IsActive = true };
        db.Employees.AddRange(employeeA, employeeB);
        db.ServiceEmployees.AddRange(
            new ServiceEmployee { ServiceId = serviceA.Id, EmployeeId = employeeA.Id },
            new ServiceEmployee { ServiceId = serviceB.Id, EmployeeId = employeeB.Id });

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

    // Found during live pre-launch testing: the admin "Vorschau" (preview) panel always reported
    // "Fehlende Fragen: Keine" for an active, required question with no answers given, even though
    // the engine's own missing-question logic is correct in isolation. Root cause: IsFinderEnabled
    // being false (the public finder toggle, which admins deliberately leave off while still
    // configuring it) makes FindServicesAsync short-circuit to all-empty results — including
    // MissingQuestions — regardless of what questions/answers are passed in. The admin preview
    // path now passes bypassEnabledCheck: true to opt out of that gate.
    [Fact]
    public async Task FindServicesAsync_FinderDisabled_ReturnsEmptyUnlessBypassed()
    {
        using var db = TestDbContextFactory.Create();

        var tenant = new Tenant { Name = "A", Slug = "finder-disabled-" + Guid.NewGuid(), IsActive = true };
        db.Tenants.Add(tenant);

        var profile = new IndustryProfile { Key = "beauty_aesthetics", Name = "Beauty", IsActive = true };
        db.IndustryProfiles.Add(profile);

        db.TenantIndustrySettings.Add(new TenantIndustrySetting
        {
            TenantId = tenant.Id,
            PrimaryIndustryProfileId = profile.Id,
            IsFinderEnabled = false,
        });

        db.ServiceFinderQuestions.Add(new ServiceFinderQuestion
        {
            TenantId = tenant.Id,
            QuestionKey = "GB-FINDER-08",
            QuestionText = "Test question",
            AnswerType = ServiceFinderAnswerType.SingleChoice,
            IsRequired = true,
            IsActive = true,
            DisplayOrder = 0,
        });

        await db.SaveChangesAsync();

        var engine = new ServiceFinderEngine(db);
        var noAnswers = new ServiceFinderAnswers(new Dictionary<string, JsonElement>());

        var normalResult = await engine.FindServicesAsync(tenant.Id, noAnswers, CancellationToken.None);
        Assert.Empty(normalResult.MissingQuestions);

        var previewResult = await engine.FindServicesAsync(tenant.Id, noAnswers, CancellationToken.None, bypassEnabledCheck: true);
        Assert.Contains(previewResult.MissingQuestions, q => q.QuestionKey == "GB-FINDER-08");
    }

    // Found during live pre-launch testing, immediately after the fix above shipped: the preview
    // still returned a completely empty result for a tenant that had never saved any
    // TenantIndustrySettings row at all (setting == null) — the first version of the bypass only
    // skipped the !IsFinderEnabled half of the check, not the setting == null half, so a tenant
    // still mid-setup (no Finder settings saved yet) got an empty preview regardless of bypass.
    [Fact]
    public async Task FindServicesAsync_NoIndustrySettingsRowAtAll_PreviewStillBypassesIt()
    {
        using var db = TestDbContextFactory.Create();
        var tenant = new Tenant { Name = "A", Slug = "no-settings-" + Guid.NewGuid(), IsActive = true };
        db.Tenants.Add(tenant);
        var category = new ServiceCategory { TenantId = tenant.Id, Name = "Face", IsActive = true };
        db.ServiceCategories.Add(category);
        var service = new Service { TenantId = tenant.Id, CategoryId = category.Id, Name = "Laser-Haarentfernung", DurationMinutes = 30, Price = 50, Currency = "EUR", IsActive = true };
        db.Services.Add(service);
        var employee = new Employee { TenantId = tenant.Id, Name = "Mara Mitarbeiterin", IsActive = true };
        db.Employees.Add(employee);
        db.ServiceEmployees.Add(new ServiceEmployee { ServiceId = service.Id, EmployeeId = employee.Id });
        await db.SaveChangesAsync();
        // Deliberately no TenantIndustrySettings row at all for this tenant.

        var engine = new ServiceFinderEngine(db);
        var noAnswers = new ServiceFinderAnswers(new Dictionary<string, JsonElement>());

        var normalResult = await engine.FindServicesAsync(tenant.Id, noAnswers, CancellationToken.None);
        Assert.Empty(normalResult.Recommendations);

        var previewResult = await engine.FindServicesAsync(tenant.Id, noAnswers, CancellationToken.None, bypassEnabledCheck: true);
        Assert.NotEmpty(previewResult.Recommendations);
    }

    // Found during live pre-launch testing: the admin guide explicitly promises the Finder
    // "never returns zero matches", but the code's own fallback-when-empty branch still filtered
    // by !Excluded — identical to the query above it except for sort order — so when every
    // service happened to get excluded by matched rules, both branches returned empty. The AI
    // explanation step then had nothing to reference and invented a non-existent service name to
    // fill the message. This confirms the promise now actually holds in code.
    [Fact]
    public async Task FindServicesAsync_AllServicesExcludedByRules_StillReturnsRecommendations()
    {
        using var db = TestDbContextFactory.Create();

        var tenant = new Tenant { Name = "A", Slug = "all-excluded-" + Guid.NewGuid(), IsActive = true };
        db.Tenants.Add(tenant);

        var profile = new IndustryProfile { Key = "beauty_aesthetics", Name = "Beauty", IsActive = true };
        db.IndustryProfiles.Add(profile);

        db.TenantIndustrySettings.Add(new TenantIndustrySetting
        {
            TenantId = tenant.Id,
            PrimaryIndustryProfileId = profile.Id,
            IsFinderEnabled = true
        });

        var category = new ServiceCategory { TenantId = tenant.Id, Name = "Face", IsActive = true };
        db.ServiceCategories.Add(category);

        var service = new Service
        {
            TenantId = tenant.Id,
            CategoryId = category.Id,
            Name = "Laser-Haarentfernung",
            DurationMinutes = 30,
            Price = 50,
            Currency = "EUR",
            IsActive = true
        };
        db.Services.Add(service);

        var employee = new Employee { TenantId = tenant.Id, Name = "Mara Mitarbeiterin", IsActive = true };
        db.Employees.Add(employee);
        db.ServiceEmployees.Add(new ServiceEmployee { ServiceId = service.Id, EmployeeId = employee.Id });

        db.ServiceFinderRules.Add(new ServiceFinderRule
        {
            TenantId = tenant.Id,
            ServiceId = service.Id,
            RuleType = ServiceFinderRuleType.ExcludeService,
            ConditionJson = "{}",
            ResultJson = "{}",
            Priority = 100,
            IsActive = true,
            ApprovalStatus = ApprovalStatus.Approved
        });

        await db.SaveChangesAsync();

        var engine = new ServiceFinderEngine(db);
        var result = await engine.FindServicesAsync(tenant.Id, new ServiceFinderAnswers(new Dictionary<string, JsonElement>()), CancellationToken.None);

        Assert.NotEmpty(result.Recommendations);
        Assert.Contains(result.Recommendations, r => r.ServiceId == service.Id);
    }

    // Found during live pre-launch testing: deactivating a service's only employee left the
    // Finder still able to recommend it — it should follow the same "needs at least one active
    // employee to be bookable" rule as the public booking page (ServiceService.GetServicesAsync).
    [Fact]
    public async Task FindServicesAsync_ServiceHasNoActiveEmployee_IsExcludedFromRecommendations()
    {
        using var db = TestDbContextFactory.Create();

        var tenant = new Tenant { Name = "A", Slug = "no-active-employee-" + Guid.NewGuid(), IsActive = true };
        db.Tenants.Add(tenant);
        var profile = new IndustryProfile { Key = "beauty_aesthetics", Name = "Beauty", IsActive = true };
        db.IndustryProfiles.Add(profile);
        db.TenantIndustrySettings.Add(new TenantIndustrySetting { TenantId = tenant.Id, PrimaryIndustryProfileId = profile.Id, IsFinderEnabled = true });
        var category = new ServiceCategory { TenantId = tenant.Id, Name = "Face", IsActive = true };
        db.ServiceCategories.Add(category);
        var unbookable = new Service { TenantId = tenant.Id, CategoryId = category.Id, Name = "Laser-Haarentfernung", DurationMinutes = 30, Price = 50, Currency = "EUR", IsActive = true };
        db.Services.Add(unbookable);
        var inactiveEmployee = new Employee { TenantId = tenant.Id, Name = "Tea Test", IsActive = false };
        db.Employees.Add(inactiveEmployee);
        db.ServiceEmployees.Add(new ServiceEmployee { ServiceId = unbookable.Id, EmployeeId = inactiveEmployee.Id });
        await db.SaveChangesAsync();

        var engine = new ServiceFinderEngine(db);
        var result = await engine.FindServicesAsync(tenant.Id, new ServiceFinderAnswers(new Dictionary<string, JsonElement>()), CancellationToken.None);

        Assert.DoesNotContain(result.Recommendations, r => r.ServiceId == unbookable.Id);
    }
}
