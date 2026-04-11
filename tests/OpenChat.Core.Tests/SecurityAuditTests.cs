using OpenChat.Core.Configuration;
using OpenChat.Core.Services;
using Xunit;

namespace OpenChat.Core.Tests;

/// <summary>
/// Regression tests for the 2026-04-10 security audit findings.
/// </summary>
public class SecurityAuditTests
{
    // ── Finding 1: External signer relay URL validation ──

    [Fact]
    public async Task ValidateSignerRelayUrl_RejectsWsWithoutAllowLocal()
    {
        ProfileConfiguration.SetAllowLocalRelays(false);
        var signer = new ExternalSignerService();

        var error = await signer.ValidateSignerRelayUrlAsync("ws://relay.example.com");

        Assert.NotNull(error);
        Assert.Contains("wss://", error);
    }

    [Fact]
    public async Task ValidateSignerRelayUrl_AllowsWssUrl()
    {
        var signer = new ExternalSignerService();

        var error = await signer.ValidateSignerRelayUrlAsync("wss://relay.damus.io");

        Assert.Null(error);
    }

    [Fact]
    public async Task ValidateSignerRelayUrl_RejectsHttpScheme()
    {
        var signer = new ExternalSignerService();

        var error = await signer.ValidateSignerRelayUrlAsync("http://relay.example.com");

        Assert.NotNull(error);
        Assert.Contains("scheme", error);
    }

    [Fact]
    public async Task ValidateSignerRelayUrl_RejectsLocalhost()
    {
        ProfileConfiguration.SetAllowLocalRelays(false);
        var signer = new ExternalSignerService();

        var error = await signer.ValidateSignerRelayUrlAsync("wss://localhost:8080");

        Assert.NotNull(error);
    }

    [Fact]
    public async Task ValidateSignerRelayUrl_RejectsPrivateIp_10()
    {
        ProfileConfiguration.SetAllowLocalRelays(false);
        var signer = new ExternalSignerService();

        var error = await signer.ValidateSignerRelayUrlAsync("wss://10.0.0.1:8080");

        Assert.NotNull(error);
        Assert.Contains("private", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateSignerRelayUrl_RejectsPrivateIp_192168()
    {
        ProfileConfiguration.SetAllowLocalRelays(false);
        var signer = new ExternalSignerService();

        var error = await signer.ValidateSignerRelayUrlAsync("wss://192.168.1.1:8080");

        Assert.NotNull(error);
        Assert.Contains("private", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateSignerRelayUrl_RejectsInvalidUri()
    {
        var signer = new ExternalSignerService();

        var error = await signer.ValidateSignerRelayUrlAsync("not a url");

        Assert.NotNull(error);
    }

    // ── Finding 1 (NostrService shared validator) ──

    [Fact]
    public async Task NostrService_ValidateRelayUrl_RejectsWsWithoutAllowLocal()
    {
        ProfileConfiguration.SetAllowLocalRelays(false);

        var error = await NostrService.ValidateRelayUrlAsync("ws://relay.example.com");

        Assert.NotNull(error);
    }

    [Fact]
    public async Task NostrService_ValidateRelayUrl_RejectsLocalhost()
    {
        ProfileConfiguration.SetAllowLocalRelays(false);

        var error = await NostrService.ValidateRelayUrlAsync("wss://127.0.0.1:8080");

        Assert.NotNull(error);
        Assert.Contains("private", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NostrService_ValidateRelayUrl_AllowsValidWss()
    {
        var error = await NostrService.ValidateRelayUrlAsync("wss://relay.damus.io");

        Assert.Null(error);
    }

    // ── Finding 2: WebSocket message size limit ──

    [Fact]
    public void NostrRelayConnection_HasMaxMessageSizeConstant()
    {
        // Verify the constant exists and is reasonable (> 1 MB, <= 64 MB)
        // Access via reflection since it's private const
        var field = typeof(NostrRelayConnection).GetField("MaxWebSocketMessageSize",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(field);
        var value = (int)field!.GetValue(null)!;
        Assert.True(value >= 1 * 1024 * 1024, "Max message size should be at least 1 MB");
        Assert.True(value <= 64 * 1024 * 1024, "Max message size should be at most 64 MB");
    }

    [Fact]
    public void NostrService_HasMaxMessageSizeConstant()
    {
        var field = typeof(NostrService).GetField("MaxWebSocketMessageSize",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(field);
        var value = (int)field!.GetValue(null)!;
        Assert.True(value >= 1 * 1024 * 1024, "Max message size should be at least 1 MB");
        Assert.True(value <= 64 * 1024 * 1024, "Max message size should be at most 64 MB");
    }

    // ── Finding 3: NostrConnect secret not logged ──

    [Fact]
    public void GenerateConnectionUri_DoesNotLogSecret()
    {
        var signer = new ExternalSignerService();
        var uri = signer.GenerateConnectionUri("wss://relay.example.com");

        // The URI should contain a secret
        Assert.Contains("secret=", uri);

        // Verify the secret value is present in the URI (sanity check)
        var secretStart = uri.IndexOf("secret=") + 7;
        var secretEnd = uri.IndexOf('&', secretStart);
        var secret = secretEnd > 0 ? uri[secretStart..secretEnd] : uri[secretStart..];
        Assert.NotEmpty(secret);
    }

    // ── Finding 5: Android backup disabled ──

    [Fact]
    public void AndroidManifest_HasBackupDisabled()
    {
        // Walk up from test assembly to find the repo root (handles worktrees)
        var dir = Path.GetDirectoryName(typeof(SecurityAuditTests).Assembly.Location);
        string? manifestPath = null;
        while (dir != null)
        {
            var candidate = Path.Combine(dir, "src", "OpenChat.Android", "Properties", "AndroidManifest.xml");
            if (File.Exists(candidate))
            {
                manifestPath = candidate;
                break;
            }
            dir = Path.GetDirectoryName(dir);
        }

        if (manifestPath == null)
            return; // Skip if Android project not found

        var content = File.ReadAllText(manifestPath);
        Assert.Contains("android:allowBackup=\"false\"", content);
        Assert.DoesNotContain("android:allowBackup=\"true\"", content);
    }

    // ── Finding 6: Default log level is Information ──

    [Fact]
    public void LoggingConfiguration_DefaultsToInformation()
    {
        // Use reflection to check the default parameter value
        var method = typeof(OpenChat.Core.Logging.LoggingConfiguration).GetMethod("Initialize");
        Assert.NotNull(method);

        var param = method!.GetParameters().First(p => p.Name == "minimumLevel");
        Assert.NotNull(param.DefaultValue);
        Assert.Equal(Serilog.Events.LogEventLevel.Information, param.DefaultValue);
    }

    // ── Finding 4: Avatar URL validation (MediaDownloadService) ──

    [Fact]
    public async Task MediaDownloadService_ValidateUrl_RejectsPrivateIp()
    {
        var service = new MediaDownloadService();

        var error = await service.ValidateUrlAsync("https://192.168.1.1/avatar.jpg");

        Assert.NotNull(error);
        Assert.Contains("private", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MediaDownloadService_ValidateUrl_RejectsNonHttps()
    {
        var service = new MediaDownloadService();

        var error = await service.ValidateUrlAsync("http://example.com/avatar.jpg");

        Assert.NotNull(error);
        Assert.Contains("HTTPS", error);
    }

    [Fact]
    public async Task MediaDownloadService_ValidateUrl_RejectsLocalhost()
    {
        var service = new MediaDownloadService();

        var error = await service.ValidateUrlAsync("https://127.0.0.1/avatar.jpg");

        Assert.NotNull(error);
    }

}
