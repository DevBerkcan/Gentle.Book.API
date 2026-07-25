namespace GentleBook.Api.Data.Entities;

public class MollieWebhookEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string MollieResourceId { get; set; } = ""; // e.g. tr_xxx (payment) or sub_xxx (subscription)
    public string ResourceType { get; set; } = "";      // "payment" | "subscription"
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }
    public string? ResultStatus { get; set; }
    public string? Notes { get; set; }
}
