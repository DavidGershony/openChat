using MarmotCs.Storage.Abstractions;
using MarmotCs.Storage.Sqlite;

namespace OpenChat.Core.Services;

/// <summary>
/// No-op secure storage that passes data through unchanged. Used for tests.
/// </summary>
internal sealed class NoOpSecureStorage : ISecureStorage
{
    public byte[] Protect(byte[] data) => data;
    public byte[] Unprotect(byte[] data) => data;
}

/// <summary>
/// Wraps <see cref="SqliteStorageProvider"/> and applies <see cref="ISecureStorage"/>
/// encryption to sensitive byte fields (exporter secrets, MLS state, welcome data,
/// message content/sender identity).
///
/// Non-sensitive fields (names, timestamps, relay URLs, event IDs) pass through unmodified.
/// </summary>
public sealed class EncryptedSqliteStorageProvider : IMdkStorageProvider, IGroupStorage, IMessageStorage, IWelcomeStorage, IDisposable
{
    private readonly SqliteStorageProvider _inner;
    private readonly ISecureStorage _secure;

    public EncryptedSqliteStorageProvider(string connectionString, ISecureStorage secureStorage)
    {
        _inner = new SqliteStorageProvider(connectionString);
        _secure = secureStorage;
    }

    public IGroupStorage Groups => this;
    public IMessageStorage Messages => this;
    public IWelcomeStorage Welcomes => this;

    // ── Helpers ──

    private byte[] Protect(byte[] data) => _secure.Protect(data);
    private byte[] Unprotect(byte[] data) => _secure.Unprotect(data);
    private byte[]? ProtectOpt(byte[]? data) => data != null ? Protect(data) : null;
    private byte[]? UnprotectOpt(byte[]? data) => data != null ? Unprotect(data) : null;

    private Group EncryptGroup(Group g) => g with
    {
        GroupData = ProtectOpt(g.GroupData),
        MlsState = ProtectOpt(g.MlsState),
    };

    private Group DecryptGroup(Group g) => g with
    {
        GroupData = UnprotectOpt(g.GroupData),
        MlsState = UnprotectOpt(g.MlsState),
    };

    // ── IGroupStorage ──

    async Task IGroupStorage.SaveGroupAsync(Group group, CancellationToken ct)
        => await _inner.Groups.SaveGroupAsync(EncryptGroup(group), ct);

    async Task<Group?> IGroupStorage.GetGroupAsync(MlsGroupId id, CancellationToken ct)
    {
        var g = await _inner.Groups.GetGroupAsync(id, ct);
        return g != null ? DecryptGroup(g) : null;
    }

    async Task<IReadOnlyList<Group>> IGroupStorage.GetGroupsAsync(GroupState? state, CancellationToken ct)
    {
        var groups = await _inner.Groups.GetGroupsAsync(state, ct);
        return groups.Select(DecryptGroup).ToList().AsReadOnly();
    }

    async Task IGroupStorage.UpdateGroupAsync(Group group, CancellationToken ct)
        => await _inner.Groups.UpdateGroupAsync(EncryptGroup(group), ct);

    Task IGroupStorage.DeleteGroupAsync(MlsGroupId id, CancellationToken ct)
        => _inner.Groups.DeleteGroupAsync(id, ct);

    Task IGroupStorage.SaveGroupRelayAsync(GroupRelay relay, CancellationToken ct)
        => _inner.Groups.SaveGroupRelayAsync(relay, ct);

    Task<IReadOnlyList<GroupRelay>> IGroupStorage.GetGroupRelaysAsync(MlsGroupId groupId, CancellationToken ct)
        => _inner.Groups.GetGroupRelaysAsync(groupId, ct);

    Task IGroupStorage.DeleteGroupRelaysAsync(MlsGroupId groupId, CancellationToken ct)
        => _inner.Groups.DeleteGroupRelaysAsync(groupId, ct);

    async Task IGroupStorage.SaveExporterSecretAsync(GroupExporterSecret secret, CancellationToken ct)
        => await _inner.Groups.SaveExporterSecretAsync(
            secret with { Secret = Protect(secret.Secret) }, ct);

    async Task<GroupExporterSecret?> IGroupStorage.GetExporterSecretAsync(MlsGroupId groupId, ulong epoch, CancellationToken ct)
    {
        var s = await _inner.Groups.GetExporterSecretAsync(groupId, epoch, ct);
        return s != null ? s with { Secret = Unprotect(s.Secret) } : null;
    }

    Task IGroupStorage.SaveAppliedCommitAsync(MlsGroupId groupId, ulong epoch, string eventId, DateTimeOffset createdAt, CancellationToken ct)
        => _inner.Groups.SaveAppliedCommitAsync(groupId, epoch, eventId, createdAt, ct);

    Task<(string EventId, DateTimeOffset CreatedAt)?> IGroupStorage.GetAppliedCommitAsync(MlsGroupId groupId, ulong epoch, CancellationToken ct)
        => _inner.Groups.GetAppliedCommitAsync(groupId, epoch, ct);

    // ── IMessageStorage ──

    async Task IMessageStorage.SaveMessageAsync(Message message, CancellationToken ct)
        => await _inner.Messages.SaveMessageAsync(message with
        {
            Content = Protect(message.Content),
            SenderIdentity = Protect(message.SenderIdentity),
        }, ct);

    async Task<Message?> IMessageStorage.GetMessageAsync(string id, CancellationToken ct)
    {
        var m = await _inner.Messages.GetMessageAsync(id, ct);
        return m != null ? m with
        {
            Content = Unprotect(m.Content),
            SenderIdentity = Unprotect(m.SenderIdentity),
        } : null;
    }

    async Task<IReadOnlyList<Message>> IMessageStorage.GetMessagesAsync(
        MlsGroupId groupId, Pagination? pagination, MessageSortOrder order, CancellationToken ct)
    {
        var messages = await _inner.Messages.GetMessagesAsync(groupId, pagination, order, ct);
        return messages.Select(m => m with
        {
            Content = Unprotect(m.Content),
            SenderIdentity = Unprotect(m.SenderIdentity),
        }).ToList().AsReadOnly();
    }

    async Task<Message?> IMessageStorage.GetLastMessageAsync(MlsGroupId groupId, CancellationToken ct)
    {
        var m = await _inner.Messages.GetLastMessageAsync(groupId, ct);
        return m != null ? m with
        {
            Content = Unprotect(m.Content),
            SenderIdentity = Unprotect(m.SenderIdentity),
        } : null;
    }

    Task IMessageStorage.SaveProcessedMessageAsync(ProcessedMessage processed, CancellationToken ct)
        => _inner.Messages.SaveProcessedMessageAsync(processed, ct);

    Task<ProcessedMessage?> IMessageStorage.GetProcessedMessageAsync(string eventId, CancellationToken ct)
        => _inner.Messages.GetProcessedMessageAsync(eventId, ct);

    Task IMessageStorage.InvalidateMessagesAfterEpochAsync(MlsGroupId groupId, ulong epoch, CancellationToken ct)
        => _inner.Messages.InvalidateMessagesAfterEpochAsync(groupId, epoch, ct);

    // ── IWelcomeStorage ──

    async Task IWelcomeStorage.SaveWelcomeAsync(Welcome welcome, CancellationToken ct)
        => await _inner.Welcomes.SaveWelcomeAsync(welcome with
        {
            WelcomeData = Protect(welcome.WelcomeData),
            GroupData = ProtectOpt(welcome.GroupData),
        }, ct);

    async Task<Welcome?> IWelcomeStorage.GetWelcomeAsync(string id, CancellationToken ct)
    {
        var w = await _inner.Welcomes.GetWelcomeAsync(id, ct);
        return w != null ? w with
        {
            WelcomeData = Unprotect(w.WelcomeData),
            GroupData = UnprotectOpt(w.GroupData),
        } : null;
    }

    async Task<IReadOnlyList<Welcome>> IWelcomeStorage.GetPendingWelcomesAsync(CancellationToken ct)
    {
        var welcomes = await _inner.Welcomes.GetPendingWelcomesAsync(ct);
        return welcomes.Select(w => w with
        {
            WelcomeData = Unprotect(w.WelcomeData),
            GroupData = UnprotectOpt(w.GroupData),
        }).ToList().AsReadOnly();
    }

    async Task IWelcomeStorage.UpdateWelcomeAsync(Welcome welcome, CancellationToken ct)
        => await _inner.Welcomes.UpdateWelcomeAsync(welcome with
        {
            WelcomeData = Protect(welcome.WelcomeData),
            GroupData = ProtectOpt(welcome.GroupData),
        }, ct);

    Task IWelcomeStorage.SaveProcessedWelcomeAsync(ProcessedWelcome processed, CancellationToken ct)
        => _inner.Welcomes.SaveProcessedWelcomeAsync(processed, ct);

    Task<ProcessedWelcome?> IWelcomeStorage.GetProcessedWelcomeAsync(string eventId, CancellationToken ct)
        => _inner.Welcomes.GetProcessedWelcomeAsync(eventId, ct);

    // ── Snapshots ──

    public Task<string> CreateSnapshotAsync(MlsGroupId groupId, CancellationToken ct = default)
        => _inner.CreateSnapshotAsync(groupId, ct);

    public Task RollbackToSnapshotAsync(string snapshotId, CancellationToken ct = default)
        => _inner.RollbackToSnapshotAsync(snapshotId, ct);

    public Task ReleaseSnapshotAsync(string snapshotId, CancellationToken ct = default)
        => _inner.ReleaseSnapshotAsync(snapshotId, ct);

    public Task PruneSnapshotsAsync(MlsGroupId groupId, int keepCount, CancellationToken ct = default)
        => _inner.PruneSnapshotsAsync(groupId, keepCount, ct);

    public void Dispose() => _inner.Dispose();
}
