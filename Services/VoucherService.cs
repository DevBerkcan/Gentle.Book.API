// Services/VoucherService.cs
// Pure balance/credit bookkeeping (Agency) — see Data/Entities/Voucher.cs. No payment processing;
// the tenant collects payment themselves and this only tracks remaining balance/sessions.
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Services;

public class VoucherService
{
    private readonly GentleBookDbContext _context;
    private readonly ILogger<VoucherService> _logger;

    public VoucherService(GentleBookDbContext context, ILogger<VoucherService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<Voucher> IssueAsync(Guid tenantId, VoucherType type, Guid? customerId, decimal? amount, int? sessions, DateTime? expiresAt, string? note, Guid issuedByPlatformUserId)
    {
        if (type == VoucherType.MonetaryValue && (amount is null or <= 0))
            throw new ArgumentException("Bitte einen gültigen Geldwert angeben.");
        if (type == VoucherType.SessionPackage && (sessions is null or <= 0))
            throw new ArgumentException("Bitte eine gültige Anzahl Sitzungen angeben.");

        var code = await GenerateUniqueCodeAsync(tenantId);
        var voucher = new Voucher
        {
            TenantId = tenantId,
            Code = code,
            CustomerId = customerId,
            Type = type,
            InitialAmount = type == VoucherType.MonetaryValue ? amount : null,
            RemainingAmount = type == VoucherType.MonetaryValue ? amount : null,
            InitialSessions = type == VoucherType.SessionPackage ? sessions : null,
            RemainingSessions = type == VoucherType.SessionPackage ? sessions : null,
            ExpiresAt = expiresAt,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            IssuedByPlatformUserId = issuedByPlatformUserId,
        };

        _context.Vouchers.Add(voucher);
        await _context.SaveChangesAsync();
        return voucher;
    }

    /// <summary>Redeems a voucher against a booking's price (MonetaryValue, deducted up to the remaining balance) or one session (SessionPackage). Throws if the code is invalid, expired, cancelled, or already exhausted.</summary>
    public async Task<Voucher> RedeemAsync(Guid tenantId, string code, decimal bookingPrice)
    {
        var voucher = await _context.Vouchers
            .FirstOrDefaultAsync(v => v.TenantId == tenantId && v.Code == code.Trim().ToUpperInvariant());
        if (voucher == null)
            throw new ArgumentException("Gutschein-Code nicht gefunden.");
        if (voucher.Status != VoucherStatus.Active)
            throw new InvalidOperationException("Dieser Gutschein ist nicht mehr gültig.");
        if (voucher.ExpiresAt.HasValue && voucher.ExpiresAt.Value < DateTime.UtcNow)
        {
            voucher.Status = VoucherStatus.Expired;
            await _context.SaveChangesAsync();
            throw new InvalidOperationException("Dieser Gutschein ist abgelaufen.");
        }

        if (voucher.Type == VoucherType.MonetaryValue)
        {
            if (voucher.RemainingAmount is not { } remaining || remaining <= 0)
                throw new InvalidOperationException("Dieser Gutschein hat kein Guthaben mehr.");
            voucher.RemainingAmount = Math.Max(0, remaining - bookingPrice);
            if (voucher.RemainingAmount <= 0) voucher.Status = VoucherStatus.Redeemed;
        }
        else
        {
            if (voucher.RemainingSessions is not { } remainingSessions || remainingSessions <= 0)
                throw new InvalidOperationException("Dieser Gutschein hat keine Sitzungen mehr.");
            voucher.RemainingSessions = remainingSessions - 1;
            if (voucher.RemainingSessions <= 0) voucher.Status = VoucherStatus.Redeemed;
        }

        await _context.SaveChangesAsync();
        _logger.LogInformation("Voucher {Code} redeemed against booking price {Price}", voucher.Code, bookingPrice);
        return voucher;
    }

    public async Task CancelAsync(Guid tenantId, Guid id)
    {
        var voucher = await _context.Vouchers.FirstOrDefaultAsync(v => v.Id == id && v.TenantId == tenantId)
            ?? throw new ArgumentException("Gutschein nicht gefunden.");
        voucher.Status = VoucherStatus.Cancelled;
        await _context.SaveChangesAsync();
    }

    private async Task<string> GenerateUniqueCodeAsync(Guid tenantId)
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O/1/I to avoid ambiguity
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var code = $"GB-{RandomSegment(4)}-{RandomSegment(4)}";
            var exists = await _context.Vouchers.AnyAsync(v => v.TenantId == tenantId && v.Code == code);
            if (!exists) return code;
        }
        throw new InvalidOperationException("Es konnte kein eindeutiger Gutschein-Code erzeugt werden. Bitte erneut versuchen.");

        string RandomSegment(int length) =>
            new(Enumerable.Range(0, length).Select(_ => chars[Random.Shared.Next(chars.Length)]).ToArray());
    }
}
