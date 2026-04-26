namespace OpenChat.Core.Configuration;

/// <summary>
/// Represents a known account in the account registry.
/// Stored in accounts.json for the account switcher UI.
/// </summary>
public class AccountEntry
{
    public string PublicKeyHex { get; set; } = string.Empty;
    public string Npub { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? AvatarUrl { get; set; }
    public bool IsRemoteSigner { get; set; }
    public DateTime AddedAt { get; set; }
    public DateTime LastActiveAt { get; set; }
}

/// <summary>
/// Root object for accounts.json — tracks all known accounts and which one is active.
/// </summary>
public class AccountRegistry
{
    public string? ActivePublicKeyHex { get; set; }
    public List<AccountEntry> Accounts { get; set; } = new();
}
