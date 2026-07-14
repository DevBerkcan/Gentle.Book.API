using System.Security.Cryptography;
using System.Text;

namespace GentleBook.Api.Services;

/// <summary>
/// HMAC-signed, short-lived tokens for the customer "Meine Buchungen" magic link.
/// Payload: email|expiryTicksUtc — signed with Jwt:Secret. No DB state required.
/// </summary>
public class CustomerPortalTokenService
{
    private readonly IConfiguration _config;

    public CustomerPortalTokenService(IConfiguration config)
    {
        _config = config;
    }

    private byte[] Key =>
        Encoding.UTF8.GetBytes(_config["Jwt:Secret"]
            ?? throw new InvalidOperationException("Jwt:Secret is not configured"));

    public string CreateToken(string email, TimeSpan? lifetime = null)
    {
        var expires = DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromHours(1));
        var payload = $"{email.Trim().ToLowerInvariant()}|{expires.Ticks}";
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        using var hmac = new HMACSHA256(Key);
        var signature = hmac.ComputeHash(payloadBytes);
        return $"{Base64Url(payloadBytes)}.{Base64Url(signature)}";
    }

    /// <summary>Returns the e-mail address if the token is valid and not expired, otherwise null.</summary>
    public string? ValidateToken(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 2) return null;

            var payloadBytes = FromBase64Url(parts[0]);
            var providedSig = FromBase64Url(parts[1]);

            using var hmac = new HMACSHA256(Key);
            var expectedSig = hmac.ComputeHash(payloadBytes);
            if (!CryptographicOperations.FixedTimeEquals(expectedSig, providedSig))
                return null;

            var payload = Encoding.UTF8.GetString(payloadBytes);
            var sep = payload.LastIndexOf('|');
            if (sep <= 0) return null;

            var email = payload[..sep];
            if (!long.TryParse(payload[(sep + 1)..], out var ticks)) return null;
            if (new DateTime(ticks, DateTimeKind.Utc) < DateTime.UtcNow) return null;

            return email;
        }
        catch
        {
            return null;
        }
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).Replace("+", "-").Replace("/", "_").TrimEnd('=');

    private static byte[] FromBase64Url(string input)
    {
        var s = input.Replace("-", "+").Replace("_", "/");
        while (s.Length % 4 != 0) s += "=";
        return Convert.FromBase64String(s);
    }
}
