// Gentle.Book.API.Tests/BrandImportUrlSecurityTests.cs
// Covers spec section 19 "Tests" → URL-Sicherheit: localhost/private-IPv4/private-IPv6/
// cloud-metadata/custom-port/file-scheme all get blocked before any outbound HTTP request is made.
using System.Net;
using GentleBook.Api.Services.BrandImport;
using Xunit;

namespace Gentle.Book.API.Tests;

public class BrandImportUrlSecurityTests
{
    private readonly UrlSecurityValidator _validator = new();

    [Theory]
    [InlineData("127.0.0.1")]      // loopback
    [InlineData("127.0.0.5")]      // loopback range
    [InlineData("10.0.0.1")]       // RFC1918
    [InlineData("172.16.0.1")]     // RFC1918
    [InlineData("172.31.255.255")] // RFC1918 upper bound
    [InlineData("192.168.1.1")]    // RFC1918
    [InlineData("169.254.169.254")] // cloud metadata (AWS/Azure/GCP IMDS)
    [InlineData("169.254.0.1")]    // link-local
    [InlineData("100.64.0.1")]     // carrier-grade NAT
    [InlineData("0.0.0.0")]
    [InlineData("255.255.255.255")]
    public void IsBlockedAddress_PrivateOrInternalIPv4_ReturnsTrue(string ip)
    {
        Assert.True(UrlSecurityValidator.IsBlockedAddress(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    public void IsBlockedAddress_PublicIPv4_ReturnsFalse(string ip)
    {
        Assert.False(UrlSecurityValidator.IsBlockedAddress(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("::1")]           // loopback
    [InlineData("fe80::1")]       // link-local
    [InlineData("fc00::1")]       // unique local
    [InlineData("fd00::1")]       // unique local
    public void IsBlockedAddress_PrivateOrInternalIPv6_ReturnsTrue(string ip)
    {
        Assert.True(UrlSecurityValidator.IsBlockedAddress(IPAddress.Parse(ip)));
    }

    [Fact]
    public void IsBlockedAddress_PublicIPv6_ReturnsFalse()
    {
        Assert.False(UrlSecurityValidator.IsBlockedAddress(IPAddress.Parse("2606:4700:4700::1111")));
    }

    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://127.0.0.1:80/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://10.0.0.5/")]
    public async Task ValidateAsync_LiteralPrivateIp_IsDenied(string url)
    {
        var result = await _validator.ValidateAsync(new Uri(url), CancellationToken.None);

        Assert.False(result.IsAllowed);
        Assert.Equal("private_address_blocked", result.ErrorCode);
    }

    [Fact]
    public async Task ValidateAsync_LocalhostHostname_IsDenied()
    {
        var result = await _validator.ValidateAsync(new Uri("http://localhost/"), CancellationToken.None);

        Assert.False(result.IsAllowed);
        Assert.Equal("host_blocked", result.ErrorCode);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/")]
    public async Task ValidateAsync_DisallowedScheme_IsDenied(string url)
    {
        var result = await _validator.ValidateAsync(new Uri(url), CancellationToken.None);

        Assert.False(result.IsAllowed);
        Assert.Equal("scheme_not_allowed", result.ErrorCode);
    }

    [Fact]
    public async Task ValidateAsync_CustomPort_IsDenied()
    {
        var result = await _validator.ValidateAsync(new Uri("http://93.184.216.34:8080/"), CancellationToken.None);

        Assert.False(result.IsAllowed);
        Assert.Equal("port_not_allowed", result.ErrorCode);
    }

    [Fact]
    public async Task ValidateAsync_PublicIpLiteralOnDefaultPort_IsAllowed()
    {
        var result = await _validator.ValidateAsync(new Uri("https://93.184.216.34/"), CancellationToken.None);

        Assert.True(result.IsAllowed);
    }
}
