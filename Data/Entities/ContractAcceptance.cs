namespace GentleBook.Api.Data.Entities;

// Immutable evidence of exactly what a tenant accepted, and under what commercial terms, at the
// moment they started a real (non-trial) Mollie checkout (LEGAL_AUDIT_2026-09-06.md BLOCKER: no
// row previously tied the accepted document VERSION + the actual price/plan/interval to a specific
// checkout attempt — only a version-constant string in an audit-log text field). Written inside
// the same flow as StartMandateFlowAsync, before the Mollie API call, never edited afterward.
public class ContractAcceptance
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid SubscriptionId { get; set; }
    public Guid ActorUserId { get; set; } // the TenantAdmin who clicked "start checkout"
    public DateTime AcceptedAtUtc { get; set; } = DateTime.UtcNow;

    public Guid TermsDocumentId { get; set; }
    public Guid PrivacyDocumentId { get; set; }
    public Guid DpaDocumentId { get; set; }

    // Commercial snapshot — what the tenant actually agreed to pay, independent of whether
    // PlanLimits pricing or the tenant's own negotiated price changes later.
    public string Plan { get; set; } = string.Empty;
    public string Interval { get; set; } = string.Empty;
    public decimal PriceSnapshot { get; set; }
    public string CurrencySnapshot { get; set; } = "EUR";

    public bool BusinessConfirmed { get; set; }

    public LegalDocument TermsDocument { get; set; } = null!;
    public LegalDocument PrivacyDocument { get; set; } = null!;
    public LegalDocument DpaDocument { get; set; } = null!;
}
