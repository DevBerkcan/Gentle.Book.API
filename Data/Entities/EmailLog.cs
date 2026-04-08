namespace GentleBook.Api.Data.Entities;

public class EmailLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? BookingId { get; set; }
    public EmailType EmailType { get; set; }
    public string RecipientEmail { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public EmailStatus Status { get; set; } = EmailStatus.Pending;
    public DateTime? SentAt { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public Booking? Booking { get; set; }
}

public enum EmailType
{
    Confirmation,
    Reminder,
    Cancellation,
    AvailabilityNotification
}

public enum EmailStatus
{
    Pending,
    Sent,
    Failed
}
