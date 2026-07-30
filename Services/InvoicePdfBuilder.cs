using GentleBook.Api.Data.Entities;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace GentleBook.Api.Services;

// Renders a GentleBook subscription invoice as a PDF. Sender identity mirrors the
// legal Impressum (Gentle.Book.UI/app/impressum/page.tsx) — keep both in sync if it changes.
// Kleinunternehmer per §19 UStG: no VAT is charged or shown.
public static class InvoicePdfBuilder
{
    private const string SellerName = "Berk-Can Atesoglu (GentleBook / GentleGroup)";
    private const string SellerStreet = "Girardetstraße 17";
    private const string SellerCity = "42109 Wuppertal";
    private const string SellerEmail = "support@gentlegroup.de";

    private static bool _licenseSet;
    private static readonly object LicenseLock = new();

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

    public static byte[] Build(Invoice invoice)
    {
        EnsureLicense();

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(2, Unit.Centimetre);
                page.DefaultTextStyle(x => x.FontSize(10));

                page.Header().Row(row =>
                {
                    row.RelativeItem().Column(col =>
                    {
                        col.Item().Text(SellerName).Bold().FontSize(12);
                        col.Item().Text(SellerStreet);
                        col.Item().Text(SellerCity);
                        col.Item().Text(SellerEmail);
                    });
                    row.ConstantItem(160).AlignRight().Text("RECHNUNG").Bold().FontSize(18);
                });

                page.Content().PaddingTop(24).Column(col =>
                {
                    col.Spacing(4);

                    col.Item().Text(invoice.RecipientName);
                    if (!string.IsNullOrWhiteSpace(invoice.RecipientStreet))
                        col.Item().Text(invoice.RecipientStreet!);
                    if (!string.IsNullOrWhiteSpace(invoice.RecipientZip) || !string.IsNullOrWhiteSpace(invoice.RecipientCity))
                        col.Item().Text($"{invoice.RecipientZip} {invoice.RecipientCity}".Trim());
                    if (!string.IsNullOrWhiteSpace(invoice.RecipientCountry))
                        col.Item().Text(invoice.RecipientCountry!);
                    if (!string.IsNullOrWhiteSpace(invoice.RecipientVatId))
                        col.Item().PaddingTop(4).Text($"USt-IdNr.: {invoice.RecipientVatId}");

                    col.Item().PaddingTop(24).Row(row =>
                    {
                        row.RelativeItem().Text($"Rechnungsnummer: {invoice.InvoiceNumber}");
                        row.RelativeItem().AlignRight().Text($"Rechnungsdatum: {invoice.IssueDate:dd.MM.yyyy}");
                    });

                    col.Item().PaddingTop(20).Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.RelativeColumn(4);
                            columns.RelativeColumn(3);
                            columns.RelativeColumn(2);
                        });

                        table.Header(header =>
                        {
                            header.Cell().Text("Leistung").Bold();
                            header.Cell().Text("Zeitraum").Bold();
                            header.Cell().AlignRight().Text("Betrag").Bold();
                            header.Cell().ColumnSpan(3).PaddingTop(4).BorderBottom(1).BorderColor(Colors.Grey.Lighten1);
                        });

                        table.Cell().PaddingTop(6).Text($"GentleBook {invoice.PlanName}-Abonnement");
                        table.Cell().PaddingTop(6).Text($"{invoice.PeriodStart:dd.MM.yyyy} – {invoice.PeriodEnd:dd.MM.yyyy}");
                        table.Cell().PaddingTop(6).AlignRight().Text($"{invoice.Amount:0.00} {invoice.Currency}");
                    });

                    col.Item().PaddingTop(12).BorderTop(1).BorderColor(Colors.Grey.Lighten1)
                        .PaddingTop(8).AlignRight().Text($"Gesamtbetrag: {invoice.Amount:0.00} {invoice.Currency}").Bold().FontSize(12);

                    col.Item().PaddingTop(28).Text("Gemäß § 19 UStG wird keine Umsatzsteuer berechnet und ausgewiesen (Kleinunternehmerregelung).")
                        .FontSize(9).FontColor(Colors.Grey.Darken1);

                    col.Item().PaddingTop(4).Text(
                        $"Bezahlt per SEPA-Lastschrift über Mollie am {invoice.IssueDate:dd.MM.yyyy} (Zahlungsreferenz: {invoice.MolliePaymentId}).")
                        .FontSize(9).FontColor(Colors.Grey.Darken1);
                });

                page.Footer().AlignCenter().Text($"{SellerName} · {SellerStreet}, {SellerCity} · {SellerEmail}")
                    .FontSize(8).FontColor(Colors.Grey.Darken1);
            });
        });

        return document.GeneratePdf();
    }
}
