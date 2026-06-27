namespace GentleBook.Api.Data.Entities;

public class SubscriptionRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;
    public string RequestedPlan { get; set; } = "";   // Starter | Professional | Agency
    public string ContactEmail { get; set; } = "";
    public string? Note { get; set; }
    public string Status { get; set; } = "Pending";   // Pending | Activated | Declined
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }
}
