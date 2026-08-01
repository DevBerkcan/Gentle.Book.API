// Services/BrandImport/UrlSecurityValidator.cs
// SSRF ("Server-Side Request Forgery") protection for the AI Brand Import feature.
//
// This is the ONLY place in the codebase today that accepts an admin-supplied external URL and
// actually issues outbound HTTP requests to it (see spec section 6 "SSRF- und URL-Sicherheit").
// Every resolved IP address — including ones discovered via redirects — is re-validated here
// before a socket connection is opened, which is what protects against DNS rebinding (an
// attacker's DNS record legitimately resolving to a public IP at validation time and then to a
// private/internal IP a few milliseconds later at actual connect time).
using System.Net;
using System.Net.Sockets;

namespace GentleBook.Api.Services.BrandImport;

public sealed record UrlValidationResult(
    bool IsAllowed,
    string? ErrorCode,
    string? ErrorMessageSafe,
    IReadOnlyList<IPAddress> ResolvedAddresses)
{
    public static UrlValidationResult Allowed(IReadOnlyList<IPAddress> addresses) =>
        new(true, null, null, addresses);

    public static UrlValidationResult Denied(string code, string messageSafe) =>
        new(false, code, messageSafe, Array.Empty<IPAddress>());
}

public interface IUrlSecurityValidator
{
    /// <summary>
    /// Validates scheme, port and — after resolving DNS — every candidate IP address of
    /// <paramref name="uri"/>. Does not open any connection itself.
    /// </summary>
    Task<UrlValidationResult> ValidateAsync(Uri uri, CancellationToken cancellationToken);
}

public sealed class UrlSecurityValidator : IUrlSecurityValidator
{
    private static readonly HashSet<string> AllowedSchemes = new(StringComparer.OrdinalIgnoreCase) { "http", "https" };

    // Hostnames that must never be resolved/contacted regardless of what DNS says about them.
    private static readonly HashSet<string> BlockedHostnames = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost",
        "metadata.google.internal", // GCP metadata
        "metadata.internal",
    };

    public async Task<UrlValidationResult> ValidateAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (!AllowedSchemes.Contains(uri.Scheme))
            return UrlValidationResult.Denied("scheme_not_allowed", "Es werden nur http:// und https:// Adressen unterstützt.");

        // Only default ports (80 for http, 443 for https) are allowed — custom ports could be
        // used to reach internal services (databases, admin panels, etc.) running on the same host.
        if (!uri.IsDefaultPort)
            return UrlValidationResult.Denied("port_not_allowed", "Diese Adresse verwendet einen nicht unterstützten Port.");

        var host = uri.Host;
        if (BlockedHostnames.Contains(host) || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
            return UrlValidationResult.Denied("host_blocked", "Diese Adresse ist nicht erlaubt.");

        IReadOnlyList<IPAddress> addresses;
        try
        {
            if (IPAddress.TryParse(host, out var literal))
            {
                addresses = new[] { literal };
            }
            else
            {
                var resolved = await Dns.GetHostAddressesAsync(host, cancellationToken);
                if (resolved.Length == 0)
                    return UrlValidationResult.Denied("dns_resolution_failed", "Die Adresse konnte nicht aufgelöst werden.");
                addresses = resolved;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return UrlValidationResult.Denied("dns_resolution_failed", "Die Adresse konnte nicht aufgelöst werden.");
        }

        // Reject if ANY resolved address is blocked — a hostname that round-robins between a
        // public and a private IP is treated as fully blocked rather than "sometimes allowed".
        foreach (var address in addresses)
        {
            if (IsBlockedAddress(address))
                return UrlValidationResult.Denied("private_address_blocked", "Diese Adresse verweist auf ein internes oder privates Netzwerk und ist nicht erlaubt.");
        }

        return UrlValidationResult.Allowed(addresses);
    }

    public static bool IsBlockedAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address)) return true;
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;
        if (address.Equals(IPAddress.None)) return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            // 10.0.0.0/8
            if (bytes[0] == 10) return true;
            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            // 169.254.0.0/16 (link-local, incl. 169.254.169.254 cloud metadata)
            if (bytes[0] == 169 && bytes[1] == 254) return true;
            // 100.64.0.0/10 (carrier-grade NAT)
            if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) return true;
            // 0.0.0.0/8
            if (bytes[0] == 0) return true;
            // 224.0.0.0/4 multicast, 240.0.0.0/4 reserved, 255.255.255.255 broadcast
            if (bytes[0] >= 224) return true;
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal) return true;
            if (address.IsIPv6SiteLocal) return true;
            if (address.IsIPv6Multicast) return true;

            var bytes = address.GetAddressBytes();
            // fc00::/7 (unique local addresses)
            if ((bytes[0] & 0xFE) == 0xFC) return true;
            // fd00:ec2::254 (AWS IMDSv2 IPv6 metadata) is covered by the fc00::/7 check above.
            return false;
        }

        return true; // unknown address family — fail closed
    }
}
