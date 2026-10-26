using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services;
using Xunit;

namespace Gentle.Book.API.Tests;

public class VoucherPdfBuilderTests
{
    private static TenantSettings Settings() => new()
    {
        TenantId = Guid.NewGuid(),
        CompanyName = "Hautliebe Laser&Aesthetics",
        PrimaryColor = "#6355E4",
        Phone = "+49 151 23456789",
        Email = "info@hautliebe.test",
        DefaultCurrency = "EUR",
    };

    [Theory]
    [InlineData(VoucherType.MonetaryValue)]
    [InlineData(VoucherType.SessionPackage)]
    [InlineData(VoucherType.PercentageDiscount)]
    public void Build_AllVoucherTypes_ProducesNonEmptyPdf(VoucherType type)
    {
        var voucher = new Voucher
        {
            Code = "GB-TEST-1234",
            Type = type,
            InitialAmount = type == VoucherType.MonetaryValue ? 50m : null,
            InitialSessions = type == VoucherType.SessionPackage ? 10 : (type == VoucherType.PercentageDiscount ? 1 : null),
            PercentageValue = type == VoucherType.PercentageDiscount ? 20m : null,
            ExpiresAt = DateTime.UtcNow.AddMonths(6),
            Note = "Danke für deine Treue!",
            IssuedAt = DateTime.UtcNow,
        };

        var bytes = VoucherPdfBuilder.Build(voucher, Settings(), "Max Mustermann", "EUR");

        Assert.NotEmpty(bytes);
        // PDF file signature
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
    }

    [Fact]
    public void Build_NoExpiryNoRecipient_StillProducesValidPdf()
    {
        var voucher = new Voucher
        {
            Code = "GB-TEST-5678",
            Type = VoucherType.MonetaryValue,
            InitialAmount = 25m,
            ExpiresAt = null,
            IssuedAt = DateTime.UtcNow,
        };

        var bytes = VoucherPdfBuilder.Build(voucher, Settings(), recipientName: null, "EUR");

        Assert.NotEmpty(bytes);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
    }
}
