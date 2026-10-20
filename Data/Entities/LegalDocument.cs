namespace GentleBook.Api.Data.Entities;

public enum LegalDocumentType
{
    Terms,   // AGB
    Privacy, // Datenschutzerklärung
    Dpa,     // Auftragsverarbeitungsvertrag
}

// An immutable, releasable legal-text version (LEGAL_AUDIT_2026-09-06.md BLOCKER: "Rechtsfassungen
// nicht unveränderlich zum einzelnen Standardcheckout archiviert"). There is deliberately no
// Update/Delete endpoint anywhere in the API for this entity — once a row exists it is permanent;
// a corrected text becomes a new row with a new Version, never an edit of an existing one. Sha256
// lets anyone verify a copy of the text against what was actually shown/accepted without trusting
// the DB alone. Registered only via SuperAdminController (platform-operator trust level) — a
// tenant/studio never creates these, since GentleBook's own AGB/Datenschutz/AVV apply platform-wide,
// not per tenant (contrast TenantSettings.PrivacyUrl/ImprintUrl, which are the *studio's own*,
// separate legal pages for its end customers).
public class LegalDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public LegalDocumentType Type { get; set; }
    public string Version { get; set; } = string.Empty;   // e.g. "2026-10-20"
    public string Language { get; set; } = "de";           // ISO 639-1
    public string FullText { get; set; } = string.Empty;
    public string Sha256Hash { get; set; } = string.Empty;
    public DateTime ReleasedAt { get; set; } = DateTime.UtcNow;
    public Guid ReleasedByUserId { get; set; }
}
