using GentleBook.Api.Data.Entities;

namespace GentleBook.Api.DTOs;

public sealed record FinderQuestionDto(
    Guid Id,
    string QuestionKey,
    string QuestionText,
    ServiceFinderAnswerType AnswerType,
    bool IsRequired,
    int DisplayOrder,
    string? ConfigJson,
    bool IsActive,
    Guid? IndustryProfileId);

public sealed record FinderRuleDto(
    Guid Id,
    Guid? ServiceId,
    ServiceFinderRuleType RuleType,
    string ConditionJson,
    string ResultJson,
    int Priority,
    bool IsActive,
    ApprovalStatus ApprovalStatus);

public sealed record ServiceGuidanceDto(
    Guid Id,
    Guid ServiceId,
    ServiceGuidanceType GuidanceType,
    string Title,
    string Content,
    ApprovalStatus ApprovalStatus,
    bool IsActive,
    DateTime? ValidFrom,
    DateTime? ValidTo);

public sealed record FinderAnswerValueDto(string Key, System.Text.Json.JsonElement Value);

public sealed record EvaluateFinderRequestDto(
    List<FinderAnswerValueDto> Answers,
    string? FreeText,
    Guid? SelectedServiceId = null,
    Guid? SelectedEmployeeId = null,
    string? DesiredDate = null);

public sealed record ServiceRecommendationDto(
    Guid ServiceId,
    string ServiceName,
    int Score,
    decimal Price,
    string Currency,
    int DurationMinutes,
    IReadOnlyList<Guid> SuggestedEmployeeIds);

public sealed record RequiredQuestionDto(
    string QuestionKey,
    string QuestionText,
    ServiceFinderAnswerType AnswerType);

public sealed record CustomerGuidanceItemDto(
    Guid GuidanceId,
    ServiceGuidanceType GuidanceType,
    string Title,
    string Content,
    string SourceLabel);

public sealed record EvaluateFinderResponseDto(
    string Message,
    IReadOnlyList<ServiceRecommendationDto> Recommendations,
    IReadOnlyList<RequiredQuestionDto> MissingQuestions,
    IReadOnlyList<CustomerGuidanceItemDto> Guidance,
    bool RequiresHumanConsultation,
    bool UsedAiFallback,
    IReadOnlyList<string> Sources,
    IReadOnlyList<Guid> SuggestedEmployeeIds);

public sealed record TenantIndustrySettingsDto(
    Guid? PrimaryIndustryProfileId,
    bool IsFinderEnabled,
    string? SettingsJson,
    IReadOnlyList<string> EnabledCapabilities);

public sealed record UpsertTenantIndustrySettingsRequestDto(
    Guid PrimaryIndustryProfileId,
    bool IsFinderEnabled,
    string? SettingsJson,
    IReadOnlyList<string> EnabledCapabilities);

public sealed record UpsertFinderQuestionsRequestDto(IReadOnlyList<FinderQuestionUpsertItemDto> Questions);

public sealed record FinderQuestionUpsertItemDto(
    Guid? Id,
    Guid? IndustryProfileId,
    string QuestionKey,
    string QuestionText,
    ServiceFinderAnswerType AnswerType,
    bool IsRequired,
    int DisplayOrder,
    string? ConfigJson,
    bool IsActive);

public sealed record UpsertFinderRulesRequestDto(IReadOnlyList<FinderRuleUpsertItemDto> Rules);

public sealed record FinderRuleUpsertItemDto(
    Guid? Id,
    Guid? ServiceId,
    ServiceFinderRuleType RuleType,
    string ConditionJson,
    string ResultJson,
    int Priority,
    bool IsActive,
    ApprovalStatus ApprovalStatus);

public sealed record UpsertServiceGuidanceRequestDto(IReadOnlyList<ServiceGuidanceUpsertItemDto> Guidance);

public sealed record ServiceGuidanceUpsertItemDto(
    Guid? Id,
    Guid ServiceId,
    ServiceGuidanceType GuidanceType,
    string Title,
    string Content,
    ApprovalStatus ApprovalStatus,
    bool IsActive,
    DateTime? ValidFrom,
    DateTime? ValidTo);

public sealed record CreateBookingDraftRequestDto(
    Guid ServiceId,
    Guid? EmployeeId,
    string BookingDate,
    string StartTime,
    CustomerInfoDto Customer,
    string? CustomerNotes);

public sealed record BookingDraftResponseDto(
    Guid DraftId,
    DateTime ExpiresAt,
    string Status,
    string ServiceName,
    string BookingDate,
    string StartTime,
    Guid? EmployeeId);

public sealed record ConfirmBookingDraftRequestDto(bool CustomerConfirmed);
