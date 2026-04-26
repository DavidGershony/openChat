using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenChat.Core.Logging;

namespace OpenChat.Core.Configuration;

/// <summary>
/// Manages the account registry (accounts.json) — the list of known accounts
/// for seamless multi-account switching. Replaces the single-entry last_user.json.
/// </summary>
public static class AccountRegistryService
{
    private static readonly ILogger _logger = LoggingConfiguration.CreateLogger<object>();

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private static AccountRegistry _registry = new();
    private static bool _loaded;

    private static string RegistryPath =>
        Path.Combine(ProfileConfiguration.RootDataDirectory, "accounts.json");

    /// <summary>
    /// Loads the account registry from disk. Auto-migrates from last_user.json if needed.
    /// Safe to call multiple times — only loads once unless <see cref="Reload"/> is called.
    /// </summary>
    public static void Load()
    {
        if (_loaded) return;

        try
        {
            if (File.Exists(RegistryPath))
            {
                var json = File.ReadAllText(RegistryPath);
                _registry = JsonSerializer.Deserialize<AccountRegistry>(json, _jsonOptions)
                            ?? new AccountRegistry();
                _loaded = true;
                _logger.LogDebug("Loaded account registry with {Count} account(s)", _registry.Accounts.Count);
                return;
            }

            // Migration: if last_user.json exists but accounts.json doesn't, migrate
            var lastUserPubKey = ProfileConfiguration.ReadLastUserPubKey();
            if (lastUserPubKey != null)
            {
                _logger.LogInformation("Migrating from last_user.json to accounts.json");
                _registry = new AccountRegistry
                {
                    ActivePublicKeyHex = lastUserPubKey,
                    Accounts = new List<AccountEntry>
                    {
                        new()
                        {
                            PublicKeyHex = lastUserPubKey,
                            AddedAt = DateTime.UtcNow,
                            LastActiveAt = DateTime.UtcNow
                        }
                    }
                };
                Save();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load account registry from {Path}; starting fresh", RegistryPath);
            _registry = new AccountRegistry();
        }

        _loaded = true;
    }

    /// <summary>
    /// Forces a reload from disk on next <see cref="Load"/> call.
    /// </summary>
    public static void Reload()
    {
        _loaded = false;
        _registry = new AccountRegistry();
    }

    /// <summary>
    /// Persists the current registry to disk.
    /// </summary>
    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(ProfileConfiguration.RootDataDirectory);
            var json = JsonSerializer.Serialize(_registry, _jsonOptions);
            File.WriteAllText(RegistryPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save account registry to {Path}", RegistryPath);
        }
    }

    /// <summary>
    /// Adds a new account or updates metadata for an existing one (upsert by PublicKeyHex).
    /// </summary>
    public static void AddOrUpdateAccount(AccountEntry entry)
    {
        Load();

        var existing = _registry.Accounts.Find(a =>
            string.Equals(a.PublicKeyHex, entry.PublicKeyHex, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            existing.Npub = entry.Npub;
            existing.DisplayName = entry.DisplayName;
            existing.AvatarUrl = entry.AvatarUrl;
            existing.IsRemoteSigner = entry.IsRemoteSigner;
            existing.LastActiveAt = entry.LastActiveAt;
        }
        else
        {
            _registry.Accounts.Add(entry);
        }

        Save();
    }

    /// <summary>
    /// Removes an account from the registry. Optionally deletes its profile data folder.
    /// </summary>
    public static void RemoveAccount(string publicKeyHex, bool deleteProfileData = false)
    {
        Load();

        _registry.Accounts.RemoveAll(a =>
            string.Equals(a.PublicKeyHex, publicKeyHex, StringComparison.OrdinalIgnoreCase));

        if (string.Equals(_registry.ActivePublicKeyHex, publicKeyHex, StringComparison.OrdinalIgnoreCase))
            _registry.ActivePublicKeyHex = null;

        Save();

        if (deleteProfileData)
        {
            try
            {
                var profileName = ProfileConfiguration.DeriveProfileName(publicKeyHex);
                var profileDir = Path.Combine(ProfileConfiguration.RootDataDirectory, "profiles", profileName);
                if (Directory.Exists(profileDir))
                {
                    Directory.Delete(profileDir, recursive: true);
                    _logger.LogInformation("Deleted profile data for {Profile}", profileName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete profile data for {PubKey}", publicKeyHex[..16]);
            }
        }
    }

    /// <summary>
    /// Sets the active account and updates its LastActiveAt timestamp.
    /// </summary>
    public static void SetActiveAccount(string publicKeyHex)
    {
        Load();

        _registry.ActivePublicKeyHex = publicKeyHex;

        var account = _registry.Accounts.Find(a =>
            string.Equals(a.PublicKeyHex, publicKeyHex, StringComparison.OrdinalIgnoreCase));
        if (account != null)
            account.LastActiveAt = DateTime.UtcNow;

        Save();
    }

    /// <summary>
    /// Clears the active account pointer (on logout). The account remains in the registry.
    /// </summary>
    public static void ClearActiveAccount()
    {
        Load();
        _registry.ActivePublicKeyHex = null;
        Save();
    }

    /// <summary>
    /// Returns the active account entry, or null if none is active.
    /// </summary>
    public static AccountEntry? GetActiveAccount()
    {
        Load();

        if (_registry.ActivePublicKeyHex == null) return null;

        return _registry.Accounts.Find(a =>
            string.Equals(a.PublicKeyHex, _registry.ActivePublicKeyHex, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns all known accounts, ordered by LastActiveAt descending.
    /// </summary>
    public static IReadOnlyList<AccountEntry> GetAccounts()
    {
        Load();
        return _registry.Accounts
            .OrderByDescending(a => a.LastActiveAt)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Returns the number of known accounts.
    /// </summary>
    public static int Count
    {
        get
        {
            Load();
            return _registry.Accounts.Count;
        }
    }
}
