using OpenChat.Core.Configuration;

namespace OpenChat.Core.Services;

/// <summary>
/// Resolves which account to pre-select in the share target activity.
/// Uses the currently active account, falling back to most-recently-active.
/// </summary>
public static class ShareAccountResolver
{
    /// <summary>
    /// Returns the account entry to pre-select for sharing.
    /// Priority: active account > most-recently-active > null (no accounts).
    /// </summary>
    public static AccountEntry? Resolve()
    {
        var active = AccountRegistryService.GetActiveAccount();
        if (active != null)
            return active;

        // No active account (e.g. after logout) — fall back to most recent
        var accounts = AccountRegistryService.GetAccounts();
        return accounts.Count > 0 ? accounts[0] : null;
    }
}
