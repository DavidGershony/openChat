using Scramble.Core.Crypto;
using Xunit;

namespace Scramble.Core.Tests;

/// <summary>
/// Tests for NIP-19 npub / nprofile decoding and nprofile encoding.
/// Test vectors derived from https://github.com/nostr-protocol/nips/blob/master/19.md.
/// </summary>
public class Nip19Tests
{
    // Canonical NIP-19 spec example: pubkey 3bf0c6...d8e7 → npub180cvv...kdg7s
    private const string SpecPubkeyHex = "3bf0c63fcb93463407af97a5e5ee64fa883d107ef9e558472c4eb9aaaefa459d";
    private const string SpecNpub = "npub180cvv07tjdrrgpa0j7j7tmnyl2yr6yr7l8j4s3evf6u64th6gkwsyjh6w6";

    // NIP-19 spec example nprofile: TLV[0]=pubkey + two TLV[1]=relay
    // Pubkey 3bf0c6... + relay wss://r.x.com + relay wss://djbas.sadkb.com
    private const string SpecNprofile =
        "nprofile1qqsrhuxx8l9ex335q7he0f09aej04zpazpl0ne2cgukyawd24mayt8gpp4mhxue69uhhytnc9e3k7mgpz4mhxue69uhkg6nzv9ejuumpv34kytnrdaksjlyr9p";

    [Fact]
    public void TryDecodePubkey_BlankInput_ReturnsFalse()
    {
        Assert.False(Nip19.TryDecodePubkey(null, out var r));
        Assert.Null(r);
        Assert.False(Nip19.TryDecodePubkey("", out r));
        Assert.Null(r);
        Assert.False(Nip19.TryDecodePubkey("   ", out r));
        Assert.Null(r);
    }

    [Fact]
    public void TryDecodePubkey_ValidNpub_ReturnsHexLowercase()
    {
        Assert.True(Nip19.TryDecodePubkey(SpecNpub, out var r));
        Assert.NotNull(r);
        Assert.Equal(SpecPubkeyHex, r!.PubkeyHex);
        Assert.Empty(r.RelayHints);
    }

    [Fact]
    public void TryDecodePubkey_NpubWithNostrUriPrefix_StripsPrefix()
    {
        Assert.True(Nip19.TryDecodePubkey("nostr:" + SpecNpub, out var r));
        Assert.Equal(SpecPubkeyHex, r!.PubkeyHex);
    }

    [Fact]
    public void TryDecodePubkey_NpubWithUppercaseUriPrefix_StripsPrefix()
    {
        Assert.True(Nip19.TryDecodePubkey("NOSTR:" + SpecNpub, out var r));
        Assert.Equal(SpecPubkeyHex, r!.PubkeyHex);
    }

    [Fact]
    public void TryDecodePubkey_NpubWithSurroundingWhitespace_Trims()
    {
        Assert.True(Nip19.TryDecodePubkey("  " + SpecNpub + "\n", out var r));
        Assert.Equal(SpecPubkeyHex, r!.PubkeyHex);
    }

    [Fact]
    public void TryDecodePubkey_NpubMixedCase_Accepted()
    {
        // Bech32 spec: encoders should output lowercase, but decoders MUST also accept all-uppercase.
        // Mixed case is technically invalid bech32 — Bech32.Decode lower-cases input first, so we accept it.
        Assert.True(Nip19.TryDecodePubkey(SpecNpub.ToUpperInvariant(), out var r));
        Assert.Equal(SpecPubkeyHex, r!.PubkeyHex);
    }

    [Fact]
    public void TryDecodePubkey_GarbageString_ReturnsFalse()
    {
        Assert.False(Nip19.TryDecodePubkey("not-a-bech32-string", out _));
        Assert.False(Nip19.TryDecodePubkey("npub1invalid", out _));
        Assert.False(Nip19.TryDecodePubkey("hello world", out _));
    }

    [Fact]
    public void TryDecodePubkey_HexPubkey_ReturnsFalse()
    {
        // Plain hex is NOT a NIP-19 entity. The caller (TryAddChatParticipant) handles hex separately.
        Assert.False(Nip19.TryDecodePubkey(SpecPubkeyHex, out _));
    }

    [Fact]
    public void TryDecodePubkey_NsecBlocked_ReturnsFalse()
    {
        // Anything with hrp other than npub/nprofile must be rejected even if valid bech32.
        // (We don't want a paste of an nsec to silently expose a private key as a chat participant.)
        // Synthesise a valid nsec from arbitrary 32 bytes.
        var nsec = Bech32.Encode("nsec", new byte[32]);
        Assert.False(Nip19.TryDecodePubkey(nsec, out _));
    }

    [Fact]
    public void TryDecodePubkey_ValidNprofile_ReturnsPubkeyAndRelayHints()
    {
        Assert.True(Nip19.TryDecodePubkey(SpecNprofile, out var r));
        Assert.NotNull(r);
        Assert.Equal(SpecPubkeyHex, r!.PubkeyHex);
        Assert.Equal(2, r.RelayHints.Count);
        Assert.Equal("wss://r.x.com", r.RelayHints[0]);
        Assert.Equal("wss://djbas.sadkb.com", r.RelayHints[1]);
    }

    [Fact]
    public void TryDecodePubkey_NprofileWithNostrUri_Works()
    {
        Assert.True(Nip19.TryDecodePubkey("nostr:" + SpecNprofile, out var r));
        Assert.Equal(SpecPubkeyHex, r!.PubkeyHex);
        Assert.Equal(2, r.RelayHints.Count);
    }

    [Fact]
    public void EncodeNprofile_RoundTrip_PreservesPubkeyAndRelays()
    {
        var encoded = Nip19.EncodeNprofile(SpecPubkeyHex, new[] { "wss://relay.example.com", "wss://r.example.org" });

        Assert.True(Nip19.TryDecodePubkey(encoded, out var r));
        Assert.Equal(SpecPubkeyHex, r!.PubkeyHex);
        Assert.Equal(2, r.RelayHints.Count);
        Assert.Equal("wss://relay.example.com", r.RelayHints[0]);
        Assert.Equal("wss://r.example.org", r.RelayHints[1]);
        Assert.StartsWith("nprofile1", encoded);
    }

    [Fact]
    public void EncodeNprofile_NoRelayHints_RoundTrips()
    {
        var encoded = Nip19.EncodeNprofile(SpecPubkeyHex);
        Assert.True(Nip19.TryDecodePubkey(encoded, out var r));
        Assert.Equal(SpecPubkeyHex, r!.PubkeyHex);
        Assert.Empty(r.RelayHints);
    }

    [Fact]
    public void EncodeNprofile_NullOrEmptyHexThrows()
    {
        Assert.Throws<ArgumentException>(() => Nip19.EncodeNprofile(""));
        Assert.Throws<ArgumentException>(() => Nip19.EncodeNprofile("  "));
        Assert.Throws<ArgumentException>(() => Nip19.EncodeNprofile(null!));
    }

    [Fact]
    public void EncodeNprofile_BadHexThrows()
    {
        Assert.Throws<ArgumentException>(() => Nip19.EncodeNprofile("zzzz"));
        Assert.Throws<ArgumentException>(() => Nip19.EncodeNprofile("ab12")); // wrong length
    }

    [Fact]
    public void EncodeNprofile_EmptyAndWhitespaceRelaysIgnored()
    {
        var encoded = Nip19.EncodeNprofile(SpecPubkeyHex, new[] { "wss://a.example", "", "   ", "wss://b.example" });
        Assert.True(Nip19.TryDecodePubkey(encoded, out var r));
        Assert.Equal(2, r!.RelayHints.Count);
        Assert.Equal("wss://a.example", r.RelayHints[0]);
        Assert.Equal("wss://b.example", r.RelayHints[1]);
    }

    [Fact]
    public void EncodeNprofile_OversizedRelayUrl_SkippedNotThrown()
    {
        var huge = "wss://" + new string('a', 300) + ".example";
        var encoded = Nip19.EncodeNprofile(SpecPubkeyHex, new[] { "wss://small.example", huge });
        Assert.True(Nip19.TryDecodePubkey(encoded, out var r));
        Assert.Single(r!.RelayHints);
        Assert.Equal("wss://small.example", r.RelayHints[0]);
    }
}
