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

// ── Chatbot widget (public/{slug}/chatbot) ─────────────────────────────
// One endpoint, one request shape, driven by which optional field is populated — the client
// (ChatbotWidget.tsx) holds the accumulated selection in its own React state and resends
// whatever's relevant on every call rather than the server tracking step-local state itself
// (AiConversation.Status still records where the conversation is, for display/enforcement, but
// isn't the source of truth the server reads back to reconstruct "what did they already pick").
public sealed record ChatbotMessageRequestDto(
    Guid? ConversationId,
    string? Message,
    Guid? SelectedServiceId,
    Guid? SelectedEmployeeId,
    bool AnyEmployee,
    string? SelectedDate,
    string? SelectedTime,
    CustomerInfoDto? Customer,
    bool ConfirmBooking = false);

public sealed record ChatbotEmployeeOptionDto(Guid EmployeeId, string Name, string? Role);

public sealed record ChatbotSlotOptionDto(string Date, string Time, string Label);

public sealed record ChatbotMessageResponseDto(
    Guid ConversationId,
    string Status,
    string Reply,
    IReadOnlyList<ServiceRecommendationDto> RecommendedServices,
    IReadOnlyList<ChatbotEmployeeOptionDto> EmployeeOptions,
    IReadOnlyList<ChatbotSlotOptionDto> SlotOptions,
    bool RequiresCustomerInfo,
    Guid? BookingDraftId,
    BookingResponseDto? ConfirmedBooking,
    bool RequiresHumanConsultation,
    bool UsedAiFallback);

// ── Design assistant (tenant/design-assistant) ──────────────────────────
// Agency-exclusive chat in the "Meine Links" editor that suggests a page template, color
// palette and CTA text from a free-text business description. Stateless on the server — like
// the public chatbot's client, the admin UI holds the conversation history itself and resends
// it each turn — there's no AiConversation/AiMessage row for this (nothing here needs to survive
// a page reload the way a customer's in-progress booking chat does).
public sealed record DesignAssistantHistoryTurnDto(string Role, string Content);

public sealed record DesignAssistantMessageRequestDto(
    string Message,
    IReadOnlyList<DesignAssistantHistoryTurnDto>? History);

public sealed record DesignSuggestionDto(
    string? PageTemplate,
    string? ColorPaletteKey,
    string? CtaText,
    string? CtaBadge);

public sealed record DesignAssistantMessageResponseDto(
    string Reply,
    DesignSuggestionDto? Suggestion,
    bool UsedAiFallback);
