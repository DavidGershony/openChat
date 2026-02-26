using OpenChat.Core.Models;

namespace OpenChat.Core.Services;

/// <summary>
/// Service for MLS group management operations.
/// </summary>
public interface IMlsService
{
    /// <summary>
    /// Initialize the MLS client with a Nostr identity.
    /// </summary>
    Task InitializeAsync(string privateKeyHex, string publicKeyHex);

    /// <summary>
    /// Generate a new KeyPackage for group invitations.
    /// </summary>
    Task<KeyPackage> GenerateKeyPackageAsync();

    /// <summary>
    /// Create a new MLS group.
    /// </summary>
    Task<MlsGroupInfo> CreateGroupAsync(string groupName);

    /// <summary>
    /// Add a member to a group using their KeyPackage.
    /// </summary>
    Task<MlsWelcome> AddMemberAsync(byte[] groupId, KeyPackage keyPackage);

    /// <summary>
    /// Process a Welcome message to join a group.
    /// </summary>
    /// <param name="welcomeData">The welcome rumor data from AddMember.</param>
    /// <param name="wrapperEventId">The kind-444 Nostr event ID that wrapped this welcome.</param>
    Task<MlsGroupInfo> ProcessWelcomeAsync(byte[] welcomeData, string wrapperEventId);

    /// <summary>
    /// Encrypt a message for a group.
    /// </summary>
    Task<byte[]> EncryptMessageAsync(byte[] groupId, string plaintext);

    /// <summary>
    /// Decrypt a message from a group.
    /// </summary>
    Task<MlsDecryptedMessage> DecryptMessageAsync(byte[] groupId, byte[] ciphertext);

    /// <summary>
    /// Process a commit message (member changes, key updates).
    /// </summary>
    Task ProcessCommitAsync(byte[] groupId, byte[] commitData);

    /// <summary>
    /// Update keys for forward secrecy.
    /// </summary>
    Task<byte[]> UpdateKeysAsync(byte[] groupId);

    /// <summary>
    /// Remove a member from a group.
    /// </summary>
    Task<byte[]> RemoveMemberAsync(byte[] groupId, string memberPublicKey);

    /// <summary>
    /// Get current group state.
    /// </summary>
    Task<MlsGroupInfo?> GetGroupInfoAsync(byte[] groupId);

    /// <summary>
    /// Export group state for persistence.
    /// </summary>
    Task<byte[]> ExportGroupStateAsync(byte[] groupId);

    /// <summary>
    /// Import group state from persistence.
    /// </summary>
    Task ImportGroupStateAsync(byte[] groupId, byte[] state);

    /// <summary>
    /// Export service-level state (signing keys, stored key package) for persistence.
    /// Returns null if not supported or no state to export.
    /// </summary>
    Task<byte[]?> ExportServiceStateAsync();

    /// <summary>
    /// Import service-level state from persistence.
    /// </summary>
    Task ImportServiceStateAsync(byte[] state);
}

public class MlsGroupInfo
{
    public byte[] GroupId { get; set; } = Array.Empty<byte>();
    public string GroupName { get; set; } = string.Empty;
    public ulong Epoch { get; set; }
    public List<string> MemberPublicKeys { get; set; } = new();
}

public class MlsWelcome
{
    public byte[] WelcomeData { get; set; } = Array.Empty<byte>();
    public string RecipientPublicKey { get; set; } = string.Empty;

    /// <summary>
    /// Commit/evolution event data to publish as kind 445 before sending the welcome.
    /// </summary>
    public byte[]? CommitData { get; set; }

    /// <summary>
    /// The KeyPackage event ID that was used for the invitation.
    /// Required for the Welcome event's 'e' tag.
    /// </summary>
    public string? KeyPackageEventId { get; set; }
}

public class MlsDecryptedMessage
{
    public string SenderPublicKey { get; set; } = string.Empty;
    public string Plaintext { get; set; } = string.Empty;
    public ulong Epoch { get; set; }
}
