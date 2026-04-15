using OpenChat.Core.Models;
using OpenChat.Core.Services;
using OpenChat.Core.Tests.TestHelpers;
using Xunit;

namespace OpenChat.Core.Tests;

public class ContactStorageTests : IAsyncLifetime
{
    private StorageService _storage = null!;
    private string _dbPath = null!;
    private static readonly string Owner = new string('a', 64);
    private static readonly string ContactA = new string('b', 64);
    private static readonly string ContactB = new string('c', 64);

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"openchat_contacts_{Guid.NewGuid()}.db");
        _storage = new StorageService(_dbPath, new MockSecureStorage());
        await _storage.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        if (File.Exists(_dbPath)) try { File.Delete(_dbPath); } catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Upsert_InsertsNewContact()
    {
        await _storage.UpsertContactsAsync(Owner, new[]
        {
            new Contact { PublicKeyHex = ContactA, Source = "group" }
        });

        var contacts = await _storage.GetContactsAsync(Owner);
        Assert.Single(contacts);
        Assert.Equal(ContactA, contacts[0].PublicKeyHex);
        Assert.Equal("group", contacts[0].Source);
    }

    [Fact]
    public async Task Upsert_IsIdempotentOnSamePubkey()
    {
        var c = new Contact { PublicKeyHex = ContactA, Source = "group" };
        await _storage.UpsertContactsAsync(Owner, new[] { c });
        await _storage.UpsertContactsAsync(Owner, new[] { c });

        var contacts = await _storage.GetContactsAsync(Owner);
        Assert.Single(contacts);
    }

    [Fact]
    public async Task Upsert_FollowSourceIsStickyOverGroup()
    {
        await _storage.UpsertContactsAsync(Owner, new[]
        {
            new Contact { PublicKeyHex = ContactA, Source = "follow", Petname = "alice" }
        });

        // Later chat-seed pass shouldn't downgrade follow → group
        await _storage.UpsertContactsAsync(Owner, new[]
        {
            new Contact { PublicKeyHex = ContactA, Source = "group" }
        });

        var contacts = await _storage.GetContactsAsync(Owner);
        Assert.Single(contacts);
        Assert.Equal("follow", contacts[0].Source);
        Assert.Equal("alice", contacts[0].Petname);
    }

    [Fact]
    public async Task Upsert_PreservesPetnameWhenLaterUpsertHasNone()
    {
        await _storage.UpsertContactsAsync(Owner, new[]
        {
            new Contact { PublicKeyHex = ContactA, Source = "follow", Petname = "alice" }
        });
        await _storage.UpsertContactsAsync(Owner, new[]
        {
            new Contact { PublicKeyHex = ContactA, Source = "follow", Petname = null }
        });

        var contacts = await _storage.GetContactsAsync(Owner);
        Assert.Equal("alice", contacts[0].Petname);
    }

    [Fact]
    public async Task Upsert_SkipsSelf()
    {
        await _storage.UpsertContactsAsync(Owner, new[]
        {
            new Contact { PublicKeyHex = Owner, Source = "group" },
            new Contact { PublicKeyHex = ContactA, Source = "group" }
        });

        var contacts = await _storage.GetContactsAsync(Owner);
        Assert.Single(contacts);
        Assert.Equal(ContactA, contacts[0].PublicKeyHex);
    }

    [Fact]
    public async Task Get_OrdersByLastInteractedDescending()
    {
        await _storage.UpsertContactsAsync(Owner, new[]
        {
            new Contact { PublicKeyHex = ContactA, Source = "group", LastInteractedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            new Contact { PublicKeyHex = ContactB, Source = "group", LastInteractedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc) }
        });

        var contacts = await _storage.GetContactsAsync(Owner);
        Assert.Equal(ContactB, contacts[0].PublicKeyHex);
        Assert.Equal(ContactA, contacts[1].PublicKeyHex);
    }

    [Fact]
    public async Task Touch_BumpsLastInteractedAt()
    {
        var old = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await _storage.UpsertContactsAsync(Owner, new[]
        {
            new Contact { PublicKeyHex = ContactA, Source = "group", LastInteractedAt = old }
        });

        await _storage.TouchContactAsync(Owner, ContactA);

        var contacts = await _storage.GetContactsAsync(Owner);
        Assert.True(contacts[0].LastInteractedAt > old);
    }

    [Fact]
    public async Task SaveFollows_MirrorsIntoContactsAsFollowSource()
    {
        await _storage.SaveFollowsAsync(Owner, new[]
        {
            new Follow { PublicKeyHex = ContactA, Petname = "alice" },
            new Follow { PublicKeyHex = ContactB }
        });

        var contacts = await _storage.GetContactsAsync(Owner);
        Assert.Equal(2, contacts.Count);
        Assert.All(contacts, c => Assert.Equal("follow", c.Source));
        Assert.Contains(contacts, c => c.PublicKeyHex == ContactA && c.Petname == "alice");
    }

    [Fact]
    public async Task Contacts_AreScopedPerOwner()
    {
        var otherOwner = "99".PadRight(64, '0');
        await _storage.UpsertContactsAsync(Owner, new[] { new Contact { PublicKeyHex = ContactA, Source = "group" } });
        await _storage.UpsertContactsAsync(otherOwner, new[] { new Contact { PublicKeyHex = ContactB, Source = "group" } });

        Assert.Single(await _storage.GetContactsAsync(Owner));
        Assert.Single(await _storage.GetContactsAsync(otherOwner));
    }
}
