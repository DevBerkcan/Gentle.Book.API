namespace GentleBook.Api.Data.Entities;

public enum ApprovalStatus
{
    Draft,
    Approved,
    Rejected,
    Archived
}

public enum ServiceGuidanceType
{
    Preparation,
    Arrival,
    Aftercare,
    Rebooking,
    Consultation,
    SeriesRecommendation,
    Cancellation,
    Deposit,
    RequiredDocument,
    CustomerReminder,
    StaffInstruction
}

public enum ServiceFinderAnswerType
{
    SingleChoice,
    MultiChoice,
    FreeText,
    YesNo,
    Number,
    DateRange,
    ImageUpload,
    EmployeeChoice,
    PriceRange,
    DurationRange
}

public enum ServiceFinderRuleType
{
    RecommendService,
    ExcludeService,
    RequireConsultation,
    RequireReferenceImage,
    RecommendEmployee,
    SuggestAddOn,
    SuggestSeries,
    RequireDeposit,
    ShowGuidance,
    AskFollowUpQuestion
}

public enum KnowledgeVisibility
{
    Public,
    Staff,
    AdminOnly
}

public enum KnowledgeSourceStatus
{
    Draft,
    Processing,
    Ready,
    Failed,
    Archived
}

public enum BookingDraftStatus
{
    Pending,
    Confirmed,
    Expired,
    Cancelled
}

public class IndustryProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<IndustryCapability> Capabilities { get; set; } = new List<IndustryCapability>();
}

public class IndustryCapability
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid IndustryProfileId { get; set; }
    public string CapabilityKey { get; set; } = string.Empty;
    public bool DefaultEnabled { get; set; } = true;

    public IndustryProfile IndustryProfile { get; set; } = null!;
}

public class TenantIndustrySetting
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid PrimaryIndustryProfileId { get; set; }
    public string? SettingsJson { get; set; }
    public bool IsFinderEnabled { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Guid? UpdatedBy { get; set; }

    public Tenant Tenant { get; set; } = null!;
    public IndustryProfile PrimaryIndustryProfile { get; set; } = null!;
}

public class TenantIndustryCapability
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string CapabilityKey { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class ServiceFinderQuestion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? IndustryProfileId { get; set; }
    public string QuestionKey { get; set; } = string.Empty;
    public string QuestionText { get; set; } = string.Empty;
    public ServiceFinderAnswerType AnswerType { get; set; } = ServiceFinderAnswerType.SingleChoice;
    public bool IsRequired { get; set; } = false;
    public int DisplayOrder { get; set; } = 0;
    public string? ConfigJson { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public IndustryProfile? IndustryProfile { get; set; }
}

public class ServiceFinderRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? ServiceId { get; set; }
    public ServiceFinderRuleType RuleType { get; set; } = ServiceFinderRuleType.RecommendService;
    public string ConditionJson { get; set; } = "{}";
    public string ResultJson { get; set; } = "{}";
    public int Priority { get; set; } = 0;
    public bool IsActive { get; set; } = true;
    public ApprovalStatus ApprovalStatus { get; set; } = ApprovalStatus.Draft;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Guid? UpdatedBy { get; set; }

    public Service? Service { get; set; }
}

public class ServiceGuidance
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ServiceId { get; set; }
    public ServiceGuidanceType GuidanceType { get; set; } = ServiceGuidanceType.Preparation;
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public ApprovalStatus ApprovalStatus { get; set; } = ApprovalStatus.Draft;
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Guid? UpdatedBy { get; set; }

    public Service Service { get; set; } = null!;
}

public class ServiceFinderBookingDraft
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ServiceId { get; set; }
    public Guid? EmployeeId { get; set; }
    public DateOnly BookingDate { get; set; }
    public TimeOnly StartTime { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? CustomerNotes { get; set; }
    public DateTime ExpiresAt { get; set; }
    public BookingDraftStatus Status { get; set; } = BookingDraftStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ConfirmedAt { get; set; }
    public Guid? ConfirmedBookingId { get; set; }
}

public class AiKnowledgeSource
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string SourceType { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? OriginalFileName { get; set; }
    public KnowledgeVisibility Visibility { get; set; } = KnowledgeVisibility.Public;
    public ApprovalStatus ApprovalStatus { get; set; } = ApprovalStatus.Draft;
    public string? Category { get; set; }
    public Guid? ServiceId { get; set; }
    public KnowledgeSourceStatus Status { get; set; } = KnowledgeSourceStatus.Draft;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<AiKnowledgeDocument> Documents { get; set; } = new List<AiKnowledgeDocument>();
}

public class AiKnowledgeDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid SourceId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Language { get; set; } = "de";
    public int Version { get; set; } = 1;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public AiKnowledgeSource Source { get; set; } = null!;
    public ICollection<AiKnowledgeChunk> Chunks { get; set; } = new List<AiKnowledgeChunk>();
}

public class AiKnowledgeChunk
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid DocumentId { get; set; }
    public string? SectionName { get; set; }
    public string Content { get; set; } = string.Empty;
    public string? VectorReference { get; set; }
    public string? MetadataJson { get; set; }

    public AiKnowledgeDocument Document { get; set; } = null!;
}

public class AiConversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? CustomerId { get; set; }
    public string Channel { get; set; } = "PublicFinder";
    public string Status { get; set; } = "Active";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<AiMessage> Messages { get; set; } = new List<AiMessage>();
}

public class AiMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public int TokenUsage { get; set; } = 0;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public AiConversation Conversation { get; set; } = null!;
}

public class AiAction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? ConversationId { get; set; }
    public string ActionType { get; set; } = string.Empty;
    public string InputJson { get; set; } = "{}";
    public string OutputJson { get; set; } = "{}";
    public string Status { get; set; } = "Pending";
    public Guid? ConfirmedBy { get; set; }
    public DateTime? ConfirmedOn { get; set; }
    public DateTime CreatedOn { get; set; } = DateTime.UtcNow;
}

public class AiUsage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Feature { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int InputTokens { get; set; } = 0;
    public int OutputTokens { get; set; } = 0;
    public decimal EstimatedCost { get; set; } = 0m;
    public DateTime CreatedOn { get; set; } = DateTime.UtcNow;
}
