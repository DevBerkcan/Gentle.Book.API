// Data/Entities/IntakeFormEntities.cs
// One "Formulare" set per tenant, gated to industries where it makes sense (see
// Configuration/IntakeFormIndustryGate.cs) — not per service, but per service *category*
// (IntakeFormField.CategoryId, null = applies to every category). FormType groups fields into
// display sections (Anamnese/Einverständnis/Fragebogen/Nachsorge) without needing separate form
// instances/tokens — still one combined form + one token per booking.
namespace GentleBook.Api.Data.Entities;

public enum IntakeFormFieldType
{
    Text,
    Textarea,
    YesNo,
    MultipleChoice,
    Checkboxes,
    Date,
}

public enum IntakeFormType
{
    Anamnese,
    Einverstaendnis,
    Fragebogen,
    Nachsorge,
}

public class IntakeFormField
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    public string Label { get; set; } = string.Empty;
    public IntakeFormFieldType FieldType { get; set; } = IntakeFormFieldType.Text;
    public IntakeFormType FormType { get; set; } = IntakeFormType.Anamnese;

    /// <summary>JSON string array of choices — only meaningful for MultipleChoice/Checkboxes.</summary>
    public string? OptionsJson { get; set; }

    /// <summary>Null = applies to every service category. Set = only shown for bookings whose Service.CategoryId matches.</summary>
    public Guid? CategoryId { get; set; }

    /// <summary>Both set together: this field is only relevant/shown when the referenced field's answer equals ConditionalOnValue.</summary>
    public Guid? ConditionalOnFieldId { get; set; }
    public string? ConditionalOnValue { get; set; }

    public bool IsRequired { get; set; } = false;
    public bool IsActive { get; set; } = true;
    public int DisplayOrder { get; set; } = 0;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public ServiceCategory? Category { get; set; }
    public IntakeFormField? ConditionalOnField { get; set; }
    public ICollection<IntakeFormAnswer> Answers { get; set; } = new List<IntakeFormAnswer>();
}

/// <summary>One customer's completed form for one specific booking.</summary>
public class IntakeFormResponse
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid BookingId { get; set; }

    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public Booking Booking { get; set; } = null!;
    public ICollection<IntakeFormAnswer> Answers { get; set; } = new List<IntakeFormAnswer>();
}

public class IntakeFormAnswer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ResponseId { get; set; }
    public Guid FieldId { get; set; }

    public string Value { get; set; } = string.Empty;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public IntakeFormResponse Response { get; set; } = null!;
    public IntakeFormField Field { get; set; } = null!;
}
