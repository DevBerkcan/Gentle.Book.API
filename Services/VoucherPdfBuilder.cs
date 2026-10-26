using GentleBook.Api.Data.Entities;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace GentleBook.Api.Services;

// Renders a studio-issued voucher as a printable/downloadable PDF — handed to a customer who paid
// in person (cash/card at the studio), not a payment receipt from GentleBook itself (GentleBook
// never processes the payment, see Data/Entities/Voucher.cs). Branded with the tenant's own
// identity (company name/color), not GentleBook's, since this document comes from the studio.
public static class VoucherPdfBuilder
{
    private const string Ink = "#14162B";
    private const string MutedGrey = "#6B7280";
    private const string LightGrey = "#F3F4F6";

    private static bool _licenseSet;
    private static readonly object LicenseLock = new();

    // Mirrors InvoicePdfBuilder's lazy-license pattern — only touches QuestPDF's native
    // renderer the first time a document is actually built.
    private static void EnsureLicense()
    {
        if (_licenseSet) return;
        lock (LicenseLock)
        {
            if (_licenseSet) return;
            QuestPDF.Settings.License = LicenseType.Community;
            _licenseSet = true;
        }
    }

    public static byte[] Build(Voucher voucher, TenantSettings settings, string? recipientName, string currency)
    {
        EnsureLicense();

        var accent = string.IsNullOrWhiteSpace(settings.PrimaryColor) ? "#6355E4" : settings.PrimaryColor;
        var companyName = string.IsNullOrWhiteSpace(settings.CompanyName) ? "Gutschein" : settings.CompanyName;

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A5.Landscape());
                page.Margin(1.5f, Unit.Centimetre);
                page.DefaultTextStyle(x => x.FontSize(10).FontColor(Ink));

                page.Content().Column(col =>
                {
                    col.Spacing(6);

                    // ── Header ──────────────────────────────────────────
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Column(brand =>
                        {
                            brand.Item().Text(companyName).Bold().FontSize(18).FontColor(Ink);
                            if (!string.IsNullOrWhiteSpace(settings.Tagline))
                                brand.Item().Text(settings.Tagline!).FontSize(8.5f).FontColor(MutedGrey);
                        });
                        row.AutoItem().AlignRight().Text("GUTSCHEIN").Bold().FontSize(22).FontColor(accent);
                    });
                    col.Item().PaddingTop(4).LineHorizontal(2).LineColor(accent);

                    // ── Value ───────────────────────────────────────────
                    col.Item().PaddingTop(10).Background(LightGrey).Padding(16).Column(valueCol =>
                    {
                        valueCol.Item().Text(ValueLabel(voucher.Type)).FontSize(9).FontColor(MutedGrey).Bold();
                        valueCol.Item().PaddingTop(2).Text(ValueText(voucher, currency)).Bold().FontSize(28).FontColor(accent);
                    });

                    // ── Recipient / note ──────────────────────────────────
                    if (!string.IsNullOrWhiteSpace(recipientName))
                    {
                        col.Item().PaddingTop(10).Text(t =>
                        {
                            t.Span("Für: ").FontSize(9.5f).FontColor(MutedGrey);
                            t.Span(recipientName).FontSize(10.5f).Bold();
                        });
                    }
                    if (!string.IsNullOrWhiteSpace(voucher.Note))
                        col.Item().PaddingTop(2).Text(voucher.Note!).Italic().FontSize(9.5f).FontColor(MutedGrey);

                    // ── Code ────────────────────────────────────────────
                    col.Item().PaddingTop(14).Border(1.5f).BorderColor(accent).Background(Colors.White).Padding(14).AlignCenter().Column(codeCol =>
                    {
                        codeCol.Item().AlignCenter().Text("GUTSCHEIN-CODE").FontSize(8).FontColor(MutedGrey).Bold();
                        codeCol.Item().AlignCenter().PaddingTop(2).Text(voucher.Code).Bold().FontSize(24).FontColor(Ink);
                    });

                    // ── Meta ────────────────────────────────────────────
                    col.Item().PaddingTop(12).Row(row =>
                    {
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Text("Ausgestellt am").FontSize(7.5f).FontColor(MutedGrey);
                            c.Item().Text(voucher.IssuedAt.ToString("dd.MM.yyyy")).FontSize(9.5f).Bold();
                        });
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Text("Gültig bis").FontSize(7.5f).FontColor(MutedGrey);
                            c.Item().Text(voucher.ExpiresAt.HasValue ? voucher.ExpiresAt.Value.ToString("dd.MM.yyyy") : "Unbegrenzt").FontSize(9.5f).Bold();
                        });
                    });

                    col.Item().PaddingTop(10).Text(
                        "Bitte bei deinem nächsten Besuch vorzeigen oder den Code nennen, um den Gutschein einzulösen.")
                        .FontSize(8.5f).FontColor(MutedGrey);
                });

                page.Footer().PaddingTop(8).Column(col =>
                {
                    col.Item().LineHorizontal(0.5f).LineColor(LightGrey);
                    col.Item().PaddingTop(6).Row(row =>
                    {
                        var contact = string.Join(" · ", new[] { settings.Address, settings.Phone, settings.Email }
                            .Where(s => !string.IsNullOrWhiteSpace(s)));
                        row.RelativeItem().Text(contact).FontSize(7.5f).FontColor(MutedGrey);
                        row.AutoItem().Text("Erstellt mit gentlebook.de").FontSize(7.5f).FontColor(MutedGrey);
                    });
                });
            });
        });

        return document.GeneratePdf();
    }

    private static string ValueLabel(VoucherType type) => type switch
    {
        VoucherType.MonetaryValue => "GUTSCHEINWERT",
        VoucherType.SessionPackage => "BEHANDLUNGEN",
        VoucherType.PercentageDiscount => "RABATT",
        _ => "WERT",
    };

    private static string ValueText(Voucher voucher, string currency) => voucher.Type switch
    {
        VoucherType.MonetaryValue => $"{voucher.InitialAmount:0.00} {currency}",
        VoucherType.SessionPackage => $"{voucher.InitialSessions}× Behandlung",
        VoucherType.PercentageDiscount => $"{voucher.PercentageValue:0.##} %",
        _ => "—",
    };
}
