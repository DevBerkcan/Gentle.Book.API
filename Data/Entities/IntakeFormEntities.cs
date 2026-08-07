// Data/Entities/IntakeFormEntities.cs
// One shared digital intake/consultation form per tenant (not per service, not per location —
// see the Agency-Ausbau-Runde-2 plan). The set of active IntakeFormField rows *is* the form;
// there is no separate "template" object. TenantAdmin and LocationAdmin can both edit the fields
// (LocationAdmin only ever sees/edits its own tenant's rows via the same global query filter as
// everywhere else); response *viewing* is location-scoped for LocationAdmin via the linked Booking.
namespace GentleBook.Api.Data.Entities;

public enum IntakeFormFieldType
{
    Text,
    Textarea,
    YesNo,
    MultipleChoice,
}

public class IntakeFormField
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    public string Label { get; set; } = string.Empty;
    public IntakeFormFieldType FieldType { get; set; } = IntakeFormFieldType.Text;

    /// <summary>JSON string array of choices — only meaningful for MultipleChoice.</summary>
    public string? OptionsJson { get; set; }

    public bool IsRequired { get; set; } = false;
    public bool IsActive { get; set; } = true;
    public int DisplayOrder { get; set; } = 0;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
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
