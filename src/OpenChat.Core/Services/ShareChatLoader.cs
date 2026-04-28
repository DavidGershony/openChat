using OpenChat.Core.Configuration;
using OpenChat.Core.Models;

namespace OpenChat.Core.Services;

/// <summary>
/// Loads chats for a given account's profile database, grouped for the share target UI:
/// Bot/Device chats first, then DM/Group chats, each sorted by LastActivityAt descending.
/// </summary>
public static class ShareChatLoader
{
    /// <summary>
    /// Loads non-archived chats from the given account's profile DB, grouped as:
    /// 1. Bot chats (devices) sorted by LastActivityAt desc
    /// 2. DM and Group chats sorted by LastActivityAt desc
    /// </summary>
    /// <param name="accountPubKeyHex">The account's public key hex to derive the profile DB path.</param>
    /// <returns>Grouped chat list (bots first, then regular chats). Empty if DB doesn't exist or has no chats.</returns>
    public static async Task<ShareChatResult> LoadAsync(string accountPubKeyHex)
    {
        var dbPath = GetDatabasePath(accountPubKeyHex);

        if (!File.Exists(dbPath))
            return ShareChatResult.Empty;

        var storage = new StorageService(dbPath);
        await storage.InitializeAsync();

        var allChats = await storage.GetAllChatsAsync();
        var chatList = allChats.ToList();

        var bots = chatList
            .Where(c => c.Type == ChatType.Bot)
            .OrderByDescending(c => c.LastActivityAt)
            .ToList();

        var regular = chatList
            .Where(c => c.Type != ChatType.Bot)
            .OrderByDescending(c => c.LastActivityAt)
            .ToList();

        return new ShareChatResult(bots, regular);
    }

    /// <summary>
    /// Derives the database path for a given account without modifying global ProfileConfiguration state.
    /// </summary>
    internal static string GetDatabasePath(string accountPubKeyHex)
    {
        var profileName = ProfileConfiguration.DeriveProfileName(accountPubKeyHex);
        return Path.Combine(
            ProfileConfiguration.RootDataDirectory,
            "profiles",
            profileName,
            "openchat.db");
    }
}

/// <summary>
/// Result of loading chats for the share target, pre-grouped into devices and regular chats.
/// </summary>
public class ShareChatResult
{
    public static readonly ShareChatResult Empty = new(new List<Chat>(), new List<Chat>());

    public ShareChatResult(IReadOnlyList<Chat> deviceChats, IReadOnlyList<Chat> regularChats)
    {
        DeviceChats = deviceChats;
        RegularChats = regularChats;
    }

    /// <summary>Bot/DVM chats — shown first in the share UI.</summary>
    public IReadOnlyList<Chat> DeviceChats { get; }

    /// <summary>DM and Group chats — shown after devices.</summary>
    public IReadOnlyList<Chat> RegularChats { get; }

    /// <summary>Total chat count across both sections.</summary>
    public int TotalCount => DeviceChats.Count + RegularChats.Count;
}
