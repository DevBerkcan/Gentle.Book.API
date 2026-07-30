// Data/Entities/Invoice.cs
namespace GentleBook.Api.Data.Entities;

// A GentleBook-issued invoice for a paid subscription period. Generated and emailed
// directly by GentleBook (InvoiceService) after a confirmed Mollie payment — no
// dependency on Gentle.Suite CRM. Recipient fields are a snapshot of TenantSettings
// at issue time so historical invoices stay accurate even if billing details change later.
public class Invoice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid SubscriptionId { get; set; }

    // Sequential, human-facing number, e.g. "2026-0001". Unique, never reused.
    public string InvoiceNumber { get; set; } = string.Empty;

    public DateTime IssueDate { get; set; } = DateTime.UtcNow;
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }

    public string PlanName { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "EUR";
    public string MolliePaymentId { get; set; } = string.Empty;

    // ── Recipient snapshot ────────────────────────────────────
    public string RecipientName { get; set; } = string.Empty;
    public string? RecipientVatId { get; set; }
    public string? RecipientStreet { get; set; }
    public string? RecipientZip { get; set; }
    public string? RecipientCity { get; set; }
    public string? RecipientCountry { get; set; }
    public string? RecipientEmail { get; set; }

    public byte[] PdfContent { get; set; } = Array.Empty<byte>();

    public bool EmailSent { get; set; }
    public DateTime? EmailSentAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
}
