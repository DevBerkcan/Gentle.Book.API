using System.Text.Json;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Services.AI;

public sealed class ServiceFinderEngine : IServiceFinderEngine
{
    private readonly GentleBookDbContext _db;

    public ServiceFinderEngine(GentleBookDbContext db)
    {
        _db = db;
    }

    public async Task<ServiceFinderResult> FindServicesAsync(
        Guid tenantId,
        ServiceFinderAnswers answers,
        CancellationToken cancellationToken)
    {
        var setting = await _db.TenantIndustrySettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);

        if (setting == null || !setting.IsFinderEnabled)
        {
            return new ServiceFinderResult(
                Array.Empty<ServiceRecommendation>(),
                Array.Empty<RequiredQuestion>(),
                Array.Empty<CustomerGuidance>(),
                RequiresHumanConsultation: false,
                Array.Empty<Guid>());
        }

        var questions = await _db.ServiceFinderQuestions
            .AsNoTracking()
            .Where(q => q.TenantId == tenantId && q.IsActive)
            .OrderBy(q => q.DisplayOrder)
            .ToListAsync(cancellationToken);

        var missingQuestions = questions
            .Where(q => q.IsRequired && !HasAnswer(answers.Values, q.QuestionKey))
            .Select(q => new RequiredQuestion(q.QuestionKey, q.QuestionText, q.AnswerType))
            .ToList();

        var services = await _db.Services
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.IsActive)
            .OrderBy(s => s.DisplayOrder)
            .Select(s => new
            {
                s.Id,
                s.Name,
                s.Price,
                s.Currency,
                s.DurationMinutes,
                EmployeeIds = s.ServiceEmployees.Select(se => se.EmployeeId).ToList()
            })
            .ToListAsync(cancellationToken);

        var rules = await _db.ServiceFinderRules
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.IsActive && r.ApprovalStatus == ApprovalStatus.Approved)
            .OrderByDescending(r => r.Priority)
            .ThenBy(r => r.CreatedAt)
            .ToListAsync(cancellationToken);

        var guidanceRows = await _db.ServiceGuidances
            .AsNoTracking()
            .Where(g => g.TenantId == tenantId
                && g.IsActive
                && g.ApprovalStatus == ApprovalStatus.Approved
                && (g.ValidFrom == null || g.ValidFrom <= DateTime.UtcNow)
                && (g.ValidTo == null || g.ValidTo >= DateTime.UtcNow))
            .ToListAsync(cancellationToken);

        var state = services.ToDictionary(
            s => s.Id,
            s => new ServiceState(s.Id, s.Name, s.Price, s.Currency, s.DurationMinutes, s.EmployeeIds));

        var requiredQuestionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var guidanceIds = new HashSet<Guid>();
        var suggestedEmployees = new HashSet<Guid>();
        var requiresConsultation = false;

        foreach (var rule in rules)
        {
            if (!ConditionEvaluator.IsMatch(rule.ConditionJson, answers.Values))
                continue;

            var result = RuleResult.Parse(rule.ResultJson);
            var targetServices = ResolveTargetServices(rule, result, state.Keys);

            if (rule.RuleType == ServiceFinderRuleType.RequireConsultation || result.RequiresHumanConsultation)
                requiresConsultation = true;

            foreach (var qKey in result.RequiredQuestionKeys)
                requiredQuestionKeys.Add(qKey);

            foreach (var gId in result.GuidanceIds)
                guidanceIds.Add(gId);

            foreach (var employeeId in result.SuggestedEmployeeIds)
                suggestedEmployees.Add(employeeId);

            foreach (var serviceId in targetServices)
            {
                if (!state.TryGetValue(serviceId, out var serviceState))
                    continue;

                var shouldExclude = rule.RuleType == ServiceFinderRuleType.ExcludeService || result.Exclude;
                if (shouldExclude)
                {
                    serviceState.Excluded = true;
                    continue;
                }

                if (!serviceState.Excluded)
                {
                    var scoreDelta = result.Score;
                    if (scoreDelta == 0 && rule.RuleType == ServiceFinderRuleType.RecommendService)
                        scoreDelta = 10;
                    serviceState.Score += scoreDelta;
                }
            }
        }

        foreach (var qKey in requiredQuestionKeys)
        {
            if (!HasAnswer(answers.Values, qKey))
            {
                var question = questions.FirstOrDefault(q => string.Equals(q.QuestionKey, qKey, StringComparison.OrdinalIgnoreCase));
                if (question != null && missingQuestions.All(m => !string.Equals(m.QuestionKey, qKey, StringComparison.OrdinalIgnoreCase)))
                    missingQuestions.Add(new RequiredQuestion(question.QuestionKey, question.QuestionText, question.AnswerType));
            }
        }

        var recommendations = state.Values
            .Where(s => !s.Excluded)
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Name)
            .Take(3)
            .Select(s => new ServiceRecommendation(
                s.ServiceId,
                s.Name,
                s.Score,
                s.Price,
                s.Currency,
                s.DurationMinutes,
                s.EmployeeIds.Take(3).ToList()))
            .ToList();

        if (recommendations.Count == 0)
        {
            recommendations = state.Values
                .Where(s => !s.Excluded)
                .OrderBy(s => s.Name)
                .Take(3)
                .Select(s => new ServiceRecommendation(
                    s.ServiceId,
                    s.Name,
                    s.Score,
                    s.Price,
                    s.Currency,
                    s.DurationMinutes,
                    s.EmployeeIds.Take(3).ToList()))
                .ToList();
        }

        var recommendedIds = recommendations.Select(r => r.ServiceId).ToHashSet();
        var selectedGuidance = guidanceRows
            .Where(g => recommendedIds.Contains(g.ServiceId) || guidanceIds.Contains(g.Id))
            .OrderBy(g => g.GuidanceType)
            .ThenBy(g => g.Title)
            .Take(8)
            .Select(g => new CustomerGuidance(
                g.Id,
                g.GuidanceType,
                g.Title,
                g.Content,
                "Vom Unternehmen freigegeben"))
            .ToList();

        foreach (var recommendation in recommendations)
        {
            foreach (var employeeId in recommendation.SuggestedEmployeeIds)
                suggestedEmployees.Add(employeeId);
        }

        return new ServiceFinderResult(
            recommendations,
            missingQuestions,
            selectedGuidance,
            requiresConsultation,
            suggestedEmployees.ToList());
    }

    private static bool HasAnswer(IReadOnlyDictionary<string, JsonElement> answers, string key)
    {
        if (!answers.TryGetValue(key, out var value))
            return false;

        if (value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.Undefined)
            return false;

        if (value.ValueKind == JsonValueKind.String)
            return !string.IsNullOrWhiteSpace(value.GetString());

        if (value.ValueKind == JsonValueKind.Array)
            return value.GetArrayLength() > 0;

        return true;
    }

    private static IReadOnlyList<Guid> ResolveTargetServices(
        ServiceFinderRule rule,
        RuleResult result,
        IEnumerable<Guid> allServiceIds)
    {
        if (result.ServiceIds.Count > 0)
            return result.ServiceIds;

        if (rule.ServiceId.HasValue)
            return new[] { rule.ServiceId.Value };

        return allServiceIds.ToList();
    }

    private sealed class ServiceState
    {
        public ServiceState(Guid serviceId, string name, decimal price, string currency, int durationMinutes, IReadOnlyList<Guid> employeeIds)
        {
            ServiceId = serviceId;
            Name = name;
            Price = price;
            Currency = currency;
            DurationMinutes = durationMinutes;
            EmployeeIds = employeeIds;
        }

        public Guid ServiceId { get; }
        public string Name { get; }
        public decimal Price { get; }
        public string Currency { get; }
        public int DurationMinutes { get; }
        public IReadOnlyList<Guid> EmployeeIds { get; }
        public int Score { get; set; }
        public bool Excluded { get; set; }
    }

    private sealed class RuleResult
    {
        public int Score { get; private set; }
        public bool Exclude { get; private set; }
        public bool RequiresHumanConsultation { get; private set; }
        public IReadOnlyList<Guid> ServiceIds { get; private set; } = Array.Empty<Guid>();
        public IReadOnlyList<Guid> GuidanceIds { get; private set; } = Array.Empty<Guid>();
        public IReadOnlyList<Guid> SuggestedEmployeeIds { get; private set; } = Array.Empty<Guid>();
        public IReadOnlyList<string> RequiredQuestionKeys { get; private set; } = Array.Empty<string>();

        public static RuleResult Parse(string json)
        {
            var result = new RuleResult();
            if (string.IsNullOrWhiteSpace(json))
                return result;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("score", out var scoreEl) && scoreEl.ValueKind == JsonValueKind.Number)
                    result.Score = scoreEl.GetInt32();

                if (root.TryGetProperty("exclude", out var excludeEl) && excludeEl.ValueKind == JsonValueKind.True)
                    result.Exclude = true;

                if (root.TryGetProperty("requiresHumanConsultation", out var consultationEl) && consultationEl.ValueKind == JsonValueKind.True)
                    result.RequiresHumanConsultation = true;

                result.ServiceIds = ParseGuidArray(root, "serviceIds");
                result.GuidanceIds = ParseGuidArray(root, "guidanceIds");
                result.SuggestedEmployeeIds = ParseGuidArray(root, "suggestedEmployeeIds");
                result.RequiredQuestionKeys = ParseStringArray(root, "requiredQuestionKeys");
            }
            catch
            {
                // Invalid JSON should not break the deterministic engine.
            }

            return result;
        }

        private static IReadOnlyList<Guid> ParseGuidArray(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out var arr) || arr.ValueKind != JsonValueKind.Array)
                return Array.Empty<Guid>();

            var list = new List<Guid>();
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && Guid.TryParse(item.GetString(), out var value))
                    list.Add(value);
            }

            return list;
        }

        private static IReadOnlyList<string> ParseStringArray(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out var arr) || arr.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();

            var list = new List<string>();
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var text = item.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        list.Add(text.Trim());
                }
            }

            return list;
        }
    }

    private static class ConditionEvaluator
    {
        public static bool IsMatch(string json, IReadOnlyDictionary<string, JsonElement> answers)
        {
            if (string.IsNullOrWhiteSpace(json))
                return true;

            try
            {
                using var doc = JsonDocument.Parse(json);
                return EvaluateElement(doc.RootElement, answers);
            }
            catch
            {
                return false;
            }
        }

        private static bool EvaluateElement(JsonElement condition, IReadOnlyDictionary<string, JsonElement> answers)
        {
            if (condition.ValueKind != JsonValueKind.Object)
                return false;

            if (condition.TryGetProperty("all", out var all) && all.ValueKind == JsonValueKind.Array)
            {
                return all.EnumerateArray().All(item => EvaluateElement(item, answers));
            }

            if (condition.TryGetProperty("any", out var any) && any.ValueKind == JsonValueKind.Array)
            {
                return any.EnumerateArray().Any(item => EvaluateElement(item, answers));
            }

            if (!condition.TryGetProperty("questionKey", out var keyEl) || keyEl.ValueKind != JsonValueKind.String)
                return false;

            var key = keyEl.GetString();
            if (string.IsNullOrWhiteSpace(key) || !answers.TryGetValue(key, out var answer))
                return false;

            var op = "equals";
            if (condition.TryGetProperty("operator", out var opEl) && opEl.ValueKind == JsonValueKind.String)
                op = opEl.GetString() ?? "equals";

            condition.TryGetProperty("value", out var expected);

            return op.ToLowerInvariant() switch
            {
                "equals" => CompareEquals(answer, expected),
                "notequals" => !CompareEquals(answer, expected),
                "contains" => CompareContains(answer, expected),
                "in" => CompareIn(answer, expected),
                "exists" => answer.ValueKind != JsonValueKind.Null && answer.ValueKind != JsonValueKind.Undefined,
                "greaterthan" => CompareNumeric(answer, expected, (a, b) => a > b),
                "lessthan" => CompareNumeric(answer, expected, (a, b) => a < b),
                _ => false,
            };
        }

        private static bool CompareEquals(JsonElement answer, JsonElement expected)
        {
            if (answer.ValueKind == JsonValueKind.String && expected.ValueKind == JsonValueKind.String)
                return string.Equals(answer.GetString(), expected.GetString(), StringComparison.OrdinalIgnoreCase);

            if (answer.ValueKind == JsonValueKind.Number && expected.ValueKind == JsonValueKind.Number)
                return answer.GetDecimal() == expected.GetDecimal();

            if ((answer.ValueKind == JsonValueKind.True || answer.ValueKind == JsonValueKind.False)
                && (expected.ValueKind == JsonValueKind.True || expected.ValueKind == JsonValueKind.False))
                return answer.GetBoolean() == expected.GetBoolean();

            return answer.ToString().Equals(expected.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool CompareContains(JsonElement answer, JsonElement expected)
        {
            if (answer.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in answer.EnumerateArray())
                {
                    if (item.ToString().Equals(expected.ToString(), StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }

            if (answer.ValueKind == JsonValueKind.String && expected.ValueKind == JsonValueKind.String)
            {
                return (answer.GetString() ?? string.Empty)
                    .Contains(expected.GetString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        private static bool CompareIn(JsonElement answer, JsonElement expected)
        {
            if (expected.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var item in expected.EnumerateArray())
            {
                if (answer.ToString().Equals(item.ToString(), StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static bool CompareNumeric(JsonElement answer, JsonElement expected, Func<decimal, decimal, bool> predicate)
        {
            if (answer.ValueKind != JsonValueKind.Number || expected.ValueKind != JsonValueKind.Number)
                return false;

            return predicate(answer.GetDecimal(), expected.GetDecimal());
        }
    }
}
