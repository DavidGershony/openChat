using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using OpenChat.Core.Logging;

namespace OpenChat.Core.Services;

/// <summary>
/// Manages authentication for the watch bridge HTTP service.
/// Uses a 6-digit pairing code with HMAC-SHA256 bearer tokens.
/// </summary>
public class BridgeAuthService
{
    private readonly ILogger<BridgeAuthService> _logger;
    private readonly IStorageService _storageService;

    private string? _pendingPairingCode;
    private DateTime _pairingCodeExpiry;
    private string? _activeToken;

    private const string SettingKeyToken = "bridge_auth_token";
    private const int PairingCodeLength = 6;
    private const int PairingCodeTtlSeconds = 60;

    public bool HasActiveToken => _activeToken != null;

    public BridgeAuthService(IStorageService storageService)
    {
        _logger = LoggingConfiguration.CreateLogger<BridgeAuthService>();
        _storageService = storageService;
    }

    /// <summary>
    /// Load a previously persisted token from storage on startup.
    /// </summary>
    public async Task LoadPersistedTokenAsync()
    {
        var token = await _storageService.GetSettingAsync(SettingKeyToken);
        if (!string.IsNullOrEmpty(token))
        {
            _activeToken = token;
            _logger.LogInformation("Loaded persisted bridge auth token");
        }
    }

    /// <summary>
    /// Generate a new 6-digit pairing code. Valid for 60 seconds.
    /// </summary>
    public string GeneratePairingCode()
    {
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString($"D{PairingCodeLength}");
        _pendingPairingCode = code;
        _pairingCodeExpiry = DateTime.UtcNow.AddSeconds(PairingCodeTtlSeconds);
        _logger.LogInformation("Generated bridge pairing code (expires in {Ttl}s)", PairingCodeTtlSeconds);
        return code;
    }

    /// <summary>
    /// Attempt to pair using the given code. Returns a bearer token on success, null on failure.
    /// </summary>
    public async Task<string?> TryPairAsync(string code)
    {
        if (_pendingPairingCode == null)
        {
            _logger.LogWarning("Pairing attempt with no pending code");
            return null;
        }

        if (DateTime.UtcNow > _pairingCodeExpiry)
        {
            _logger.LogWarning("Pairing code expired");
            _pendingPairingCode = null;
            return null;
        }

        if (!string.Equals(code, _pendingPairingCode, StringComparison.Ordinal))
        {
            _logger.LogWarning("Invalid pairing code");
            return null;
        }

        // Code is valid — generate token
        _pendingPairingCode = null;
        var nonce = RandomNumberGenerator.GetBytes(32);
        var codeBytes = System.Text.Encoding.UTF8.GetBytes(code);
        using var hmac = new HMACSHA256(nonce);
        var hash = hmac.ComputeHash(codeBytes);
        var token = Convert.ToBase64String(hash);

        _activeToken = token;
        await _storageService.SaveSettingAsync(SettingKeyToken, token);
        _logger.LogInformation("Watch paired successfully");

        return token;
    }

    /// <summary>
    /// Validate a bearer token from an incoming request.
    /// </summary>
    public bool ValidateToken(string token)
    {
        if (_activeToken == null) return false;
        return string.Equals(token, _activeToken, StringComparison.Ordinal);
    }

    /// <summary>
    /// Extract bearer token from an Authorization header value.
    /// </summary>
    public static string? ExtractBearerToken(string? authorizationHeader)
    {
        if (string.IsNullOrEmpty(authorizationHeader)) return null;
        if (!authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        return authorizationHeader["Bearer ".Length..].Trim();
    }

    /// <summary>
    /// Unpair the watch — invalidate and remove the token.
    /// </summary>
    public async Task UnpairAsync()
    {
        _activeToken = null;
        _pendingPairingCode = null;
        await _storageService.SaveSettingAsync(SettingKeyToken, "");
        _logger.LogInformation("Watch unpaired, bridge token invalidated");
    }
}
