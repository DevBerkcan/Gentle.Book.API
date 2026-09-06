namespace GentleBook.Api.Data.Entities;

public class SubscriptionRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;
    public string RequestedPlan { get; set; } = "";   // Starter | Professional | Agency
    public SubscriptionInterval Interval { get; set; } = SubscriptionInterval.Monthly;
    public string ContactEmail { get; set; } = "";
    public string? Note { get; set; }
    public string Status { get; set; } = "Pending";   // Pending | Offered | Accepted | Activated | Declined
    public decimal? OfferedMonthlyPrice { get; set; }
    public decimal? OfferedAnnualPrice { get; set; }
    public DateTime? OfferedAt { get; set; }
    public DateTime? OfferExpiresAt { get; set; }
    public SubscriptionInterval? AcceptedInterval { get; set; }
    public decimal? AcceptedPrice { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public string? AcceptedTermsVersion { get; set; }
    public Guid? AcceptedByUserId { get; set; }
    public string? AcceptedByEmail { get; set; }
    public string? AcceptedIpAddress { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }
}
