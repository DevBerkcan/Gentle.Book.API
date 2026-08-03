using System.Security.Cryptography;
using System.Text;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Services;

// Agency-exclusive public API key management. Same raw-token/hash split as
// PasswordResetToken/TrialAccessInvitation: the raw key is returned to the caller exactly
// once at creation time and never stored — only its SHA-256 hash is persisted.
public class ApiKeyService
{
    private const string KeyPrefixMarker = "gb_live_";

    private readonly GentleBookDbContext _db;

    public ApiKeyService(GentleBookDbContext db)
    {
        _db = db;
    }

    public record CreatedApiKey(Guid Id, string Name, string RawKey, string KeyPrefix, DateTime CreatedAt);
    public record ApiKeySummary(Guid Id, string Name, string KeyPrefix, DateTime CreatedAt, DateTime? LastUsedAt, DateTime? RevokedAt);

    public async Task<CreatedApiKey> GenerateAsync(Guid tenantId, string name)
    {
        var rawSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-").Replace("/", "_").Replace("=", "");
        var rawKey = $"{KeyPrefixMarker}{rawSecret}";
        var hash = Hash(rawKey);
        var keyPrefix = rawKey[..Math.Min(rawKey.Length, 14)];

        var entity = new ApiKey
        {
            TenantId = tenantId,
            Name = name.Trim(),
            KeyHash = hash,
            KeyPrefix = keyPrefix,
            CreatedAt = DateTime.UtcNow,
        };
        _db.ApiKeys.Add(entity);
        await _db.SaveChangesAsync();

        return new CreatedApiKey(entity.Id, entity.Name, rawKey, entity.KeyPrefix, entity.CreatedAt);
    }

    /// <summary>Returns the owning TenantId when the raw key is valid, active and unrevoked — null otherwise.</summary>
    public async Task<Guid?> ValidateAsync(string? rawKey)
    {
        if (string.IsNullOrWhiteSpace(rawKey) || !rawKey.StartsWith(KeyPrefixMarker))
            return null;

        var hash = Hash(rawKey);
        var key = await _db.ApiKeys.FirstOrDefaultAsync(k => k.KeyHash == hash && k.RevokedAt == null);
        if (key == null)
            return null;

        // Best-effort last-used tracking — never block/slow the actual request on this.
        key.LastUsedAt = DateTime.UtcNow;
        try { await _db.SaveChangesAsync(); } catch { /* non-critical */ }

        return key.TenantId;
    }

    public async Task<List<ApiKeySummary>> ListAsync(Guid tenantId) =>
        await _db.ApiKeys
            .Where(k => k.TenantId == tenantId)
            .OrderByDescending(k => k.CreatedAt)
            .Select(k => new ApiKeySummary(k.Id, k.Name, k.KeyPrefix, k.CreatedAt, k.LastUsedAt, k.RevokedAt))
            .ToListAsync();

    public async Task<bool> RevokeAsync(Guid tenantId, Guid keyId)
    {
        var key = await _db.ApiKeys.FirstOrDefaultAsync(k => k.Id == keyId && k.TenantId == tenantId);
        if (key == null || key.RevokedAt != null)
            return false;

        key.RevokedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    private static string Hash(string rawKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey)));
}
