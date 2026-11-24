using GentleBook.Api.Data.Entities;

namespace GentleBook.Api.Configuration;

// "Preisrechner" link: a per-studio external URL (the studio's own price calculator, hosted
// outside GentleBook) that only a SuperAdmin sets, and that TenantAdmins/LocationAdmins open
// from the admin header in a new tab. Professional + Agency only — deliberately NOT Trial and
// NOT Starter (product decision 2026-11-24). On a downgrade the stored URL stays in the DB but
// is never handed out; after a re-upgrade the same link reappears without anyone re-entering it.
public static class PriceCalculatorGate
{
    public const int MaxUrlLength = 2048;

    public static bool IsAllowed(SubscriptionPlan plan) =>
        plan is SubscriptionPlan.Professional or SubscriptionPlan.Agency;

    /// <summary>
    /// Normalizes a SuperAdmin-entered URL. Returns (null, null) for "remove", (url, null) for a
    /// valid absolute https URL with a host, or (null, error) with a German message otherwise.
    /// HTTPS-only for the same stored-XSS reason as TenantSettings.PrivacyUrl — the value ends up
    /// as an href in every admin's header.
    /// </summary>
    public static (string? Url, string? Error) Normalize(string? input)
    {
        var trimmed = input?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return (null, null);

        if (trimmed.Length > MaxUrlLength)
            return (null, $"Die Preisrechner-URL darf höchstens {MaxUrlLength} Zeichen lang sein.");

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(uri.Host)
            || !uri.Host.Contains('.'))
        {
            return (null, "Bitte eine vollständige HTTPS-Adresse eingeben, z. B. https://preisrechner.beispiel.de");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
            return (null, "Die Preisrechner-URL darf keine Zugangsdaten (benutzer:passwort@) enthalten.");

        return (uri.AbsoluteUri, null);
    }
}
