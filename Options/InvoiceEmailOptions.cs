namespace GentleBook.Api.Options;

// Separate SMTP identity used only for subscription invoice emails (invoice@gentlebook.de),
// distinct from the general noreply@gentlegroup.de account in EmailOptions.
public class InvoiceEmailOptions
{
    public string SmtpServer { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public string SmtpUsername { get; set; } = string.Empty;
    public string SmtpPassword { get; set; } = string.Empty;
    public string SenderEmail { get; set; } = string.Empty;
    public string SenderName { get; set; } = string.Empty;
}
