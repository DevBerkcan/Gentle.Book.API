using System.Reflection;
using GentleBook.Api.Data.Entities;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace GentleBook.Api.Services;

// Renders a GentleBook subscription invoice as a PDF. Brand colors/wordmark match the
// official kit in gentlebook-website/public/brand (SVG source: #6355E4 purple, #17A398
// teal, #14162B ink) — keep in sync if the brand kit changes. Sender identity mirrors the
// legal Impressum (Gentle.Book.UI/app/impressum/page.tsx). Kleinunternehmer per §19 UStG:
// no VAT is charged or shown.
public static class InvoicePdfBuilder
{
    private const string BrandPurple = "#6355E4";
    private const string BrandTeal = "#17A398";
    private const string BrandInk = "#14162B";
    private const string MutedGrey = "#6B7280";
    private const string LightGrey = "#F3F4F6";

    private const string SellerName = "Berk-Can Atesoglu (GentleGroup)";
    private const string SellerStreet = "Girardetstraße 17";
    private const string SellerCity = "42109 Wuppertal";
    private const string SellerEmail = "support@gentlegroup.de";
    private const string SystemUrl = "https://www.gentlebook.de";

    private static bool _licenseSet;
    private static readonly object LicenseLock = new();
    private static byte[]? _logoBytes;

    // Deliberately NOT set at app startup (Program.cs): QuestPDF's native Skia renderer
    // is only touched the first time a document is actually built. Keeping this lazy means
    // a hosting environment that can't load the native library only breaks PDF generation
    // (caught by InvoiceService's caller) instead of taking the whole app down at boot.
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

    // Embedded (not a loose deployed file) so a future deploy can never accidentally omit it.
    private static byte[] LogoBytes
    {
        get
        {
            if (_logoBytes != null) return _logoBytes;
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("GentleBook.Api.Assets.gentlebook-mark.png");
            using var ms = new MemoryStream();
            stream?.CopyTo(ms);
            return _logoBytes = ms.ToArray();
        }
    }

    public static byte[] Build(Invoice invoice)
    {
        EnsureLicense();

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(2, Unit.Centimetre);
                page.DefaultTextStyle(x => x.FontSize(10).FontColor(BrandInk));

                page.Header().Column(col =>
                {
                    col.Item().Row(row =>
                    {
                        row.AutoItem().Width(34).Height(34).Image(LogoBytes);
                        row.ConstantItem(10);
                        row.RelativeItem().Column(brand =>
                        {
                            brand.Item().Text("gentlebook").Bold().FontSize(20).FontColor(BrandInk);
                            brand.Item().Text("Online Buchungssystem für Barbershops, Salons & Studios")
                                .FontSize(8).FontColor(MutedGrey);
                        });
                        row.ConstantItem(150).AlignRight().Column(title =>
                        {
                            title.Item().AlignRight().Text("RECHNUNG").Bold().FontSize(20).FontColor(BrandPurple);
                            title.Item().AlignRight().Text(invoice.InvoiceNumber).FontSize(10).FontColor(MutedGrey);
                        });
                    });
                    col.Item().PaddingTop(10).LineHorizontal(2).LineColor(BrandPurple);
                });

                page.Content().PaddingTop(20).Column(col =>
                {
                    col.Spacing(4);

                    // ── Sender / Recipient ────────────────────────────────
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Column(sender =>
                        {
                            sender.Item().Text("Rechnungssteller").FontSize(8).FontColor(MutedGrey).Bold();
                            sender.Item().PaddingTop(2).Text(SellerName).FontSize(9.5f).Bold();
                            sender.Item().Text(SellerStreet).FontSize(9).FontColor(MutedGrey);
                            sender.Item().Text(SellerCity).FontSize(9).FontColor(MutedGrey);
                            sender.Item().Text(SellerEmail).FontSize(9).FontColor(MutedGrey);
                        });
                        row.RelativeItem().Column(recipient =>
                        {
                            recipient.Item().Text("Rechnungsempfänger").FontSize(8).FontColor(MutedGrey).Bold();
                            recipient.Item().PaddingTop(2).Text(invoice.RecipientName).FontSize(9.5f).Bold();
                            if (!string.IsNullOrWhiteSpace(invoice.RecipientStreet))
                                recipient.Item().Text(invoice.RecipientStreet!).FontSize(9).FontColor(MutedGrey);
                            if (!string.IsNullOrWhiteSpace(invoice.RecipientZip) || !string.IsNullOrWhiteSpace(invoice.RecipientCity))
                                recipient.Item().Text($"{invoice.RecipientZip} {invoice.RecipientCity}".Trim()).FontSize(9).FontColor(MutedGrey);
                            if (!string.IsNullOrWhiteSpace(invoice.RecipientCountry))
                                recipient.Item().Text(invoice.RecipientCountry!).FontSize(9).FontColor(MutedGrey);
                            if (!string.IsNullOrWhiteSpace(invoice.RecipientVatId))
                                recipient.Item().PaddingTop(2).Text($"USt-IdNr.: {invoice.RecipientVatId}").FontSize(9).FontColor(MutedGrey);
                        });
                    });

                    // ── Metadata strip ─────────────────────────────────────
                    col.Item().PaddingTop(18).Background(LightGrey).Padding(12).Row(row =>
                    {
                        void Meta(string label, string value, int weight, float fontSize = 9.5f)
                        {
                            row.RelativeItem(weight).Column(c =>
                            {
                                c.Item().Text(label).FontSize(7.5f).FontColor(MutedGrey).Bold();
                                c.Item().PaddingTop(1).Text(value).FontSize(fontSize).Bold().FontColor(BrandInk);
                            });
                        }
                        Meta("RECHNUNGSDATUM", invoice.IssueDate.ToString("dd.MM.yyyy"), 2);
                        Meta("LEISTUNGSZEITRAUM", $"{invoice.PeriodStart:dd.MM.yyyy} – {invoice.PeriodEnd:dd.MM.yyyy}", 3);
                        Meta("ZAHLUNGSART", "SEPA-Lastschrift (Mollie)", 3);
                        Meta("ZAHLUNGSREFERENZ", invoice.MolliePaymentId, 4, 8f);
                    });

                    // ── Line items ─────────────────────────────────────────
                    col.Item().PaddingTop(24).Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.RelativeColumn(5);
                            columns.RelativeColumn(3);
                            columns.RelativeColumn(2);
                        });

                        table.Header(header =>
                        {
                            header.Cell().Background(BrandInk).Padding(8).Text("Leistung").FontColor(Colors.White).Bold().FontSize(9);
                            header.Cell().Background(BrandInk).Padding(8).Text("Zeitraum").FontColor(Colors.White).Bold().FontSize(9);
                            header.Cell().Background(BrandInk).Padding(8).AlignRight().Text("Betrag").FontColor(Colors.White).Bold().FontSize(9);
                        });

                        table.Cell().Padding(8).BorderBottom(1).BorderColor(LightGrey).Column(c =>
                        {
                            // Invoice has no stored Interval column — derived from the period
                            // span instead (monthly periods are ~28-31 days, yearly ~365-366),
                            // so an annual subscriber's invoice never mislabels itself monthly.
                            var isYearly = (invoice.PeriodEnd - invoice.PeriodStart).TotalDays > 60;
                            var intervalLabel = isYearly ? "Jährliches Abonnement" : "Monatliches Abonnement";
                            c.Item().Text($"GentleBook {invoice.PlanName}-Abonnement").Bold().FontSize(10);
                            c.Item().Text($"{intervalLabel} für Ihr Buchungssystem").FontSize(8).FontColor(MutedGrey);
                        });
                        table.Cell().Padding(8).BorderBottom(1).BorderColor(LightGrey)
                            .Text($"{invoice.PeriodStart:dd.MM.yyyy} –\n{invoice.PeriodEnd:dd.MM.yyyy}").FontSize(9);
                        table.Cell().Padding(8).BorderBottom(1).BorderColor(LightGrey)
                            .AlignRight().Text($"{invoice.Amount:0.00} {invoice.Currency}").FontSize(9.5f).Bold();
                    });

                    // ── Total ──────────────────────────────────────────────
                    col.Item().PaddingTop(10).AlignRight().Width(220).Background(BrandPurple).Padding(12).Row(row =>
                    {
                        row.RelativeItem().Text("Gesamtbetrag").FontColor(Colors.White).FontSize(11).Bold();
                        row.AutoItem().Text($"{invoice.Amount:0.00} {invoice.Currency}").FontColor(Colors.White).FontSize(13).Bold();
                    });

                    // ── Notes ──────────────────────────────────────────────
                    col.Item().PaddingTop(28).Text("Gemäß § 19 UStG wird keine Umsatzsteuer berechnet und ausgewiesen (Kleinunternehmerregelung).")
                        .FontSize(8.5f).FontColor(MutedGrey);
                    col.Item().PaddingTop(2).Text(
                        $"Diese Rechnung wurde automatisch nach erfolgreicher SEPA-Zahlung über Mollie erstellt (Zahlungsreferenz {invoice.MolliePaymentId}). Ein separater Zahlungseingang ist nicht erforderlich.")
                        .FontSize(8.5f).FontColor(MutedGrey);

                    // ── CTA box ────────────────────────────────────────────
                    col.Item().PaddingTop(20).Background(LightGrey).Padding(14).Row(row =>
                    {
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Text("Dein Abonnement verwalten").Bold().FontSize(10);
                            c.Item().Text("Rechnungen, Plan und Kündigung findest du jederzeit in deinem Konto.").FontSize(8.5f).FontColor(MutedGrey);
                        });
                        row.AutoItem().AlignMiddle().Hyperlink($"{SystemUrl}/admin/subscription")
                            .Text($"{SystemUrl}/admin/subscription").FontColor(BrandPurple).Bold().FontSize(9);
                    });
                });

                page.Footer().PaddingTop(12).Column(col =>
                {
                    col.Item().LineHorizontal(0.5f).LineColor(LightGrey);
                    col.Item().PaddingTop(8).Row(row =>
                    {
                        row.RelativeItem().Text($"{SellerName} · {SellerStreet}, {SellerCity} · {SellerEmail} · ")
                            .FontSize(7.5f).FontColor(MutedGrey);
                        row.AutoItem().Hyperlink(SystemUrl).Text("gentlebook.de").FontSize(7.5f).FontColor(BrandTeal);
                        row.AutoItem().PaddingLeft(20).Text(t =>
                        {
                            t.CurrentPageNumber().FontSize(7.5f).FontColor(MutedGrey);
                            t.Span(" / ").FontSize(7.5f).FontColor(MutedGrey);
                            t.TotalPages().FontSize(7.5f).FontColor(MutedGrey);
                        });
                    });
                });
            });
        });

        return document.GeneratePdf();
    }
}
