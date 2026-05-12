namespace Scramble.Core.Crypto;

/// <summary>
/// NIP-19 bech32-encoded entity decoder/encoder.
///
/// Supports:
/// <list type="bullet">
///   <item><description><c>npub1...</c> — 32-byte public key, no TLV</description></item>
///   <item><description><c>nprofile1...</c> — TLV: type 0 (32-byte pubkey, required), type 1 (relay URL, repeatable, optional)</description></item>
///   <item><description><c>nostr:</c> URI prefix is stripped before decoding</description></item>
/// </list>
///
/// See https://github.com/nostr-protocol/nips/blob/master/19.md.
/// </summary>
public static class Nip19
{
    private const string NostrUriPrefix = "nostr:";

    /// <summary>TLV type for a 32-byte public key inside an nprofile entity.</summary>
    private const byte TlvTypeSpecial = 0;

    /// <summary>TLV type for a relay URL hint (repeatable) inside an nprofile entity.</summary>
    private const byte TlvTypeRelay = 1;

    /// <summary>
    /// Result of decoding a NIP-19 npub or nprofile entity.
    /// </summary>
    /// <param name="PubkeyHex">Lower-case 64-char hex public key.</param>
    /// <param name="RelayHints">Relay URLs hinted by an nprofile, in order of appearance. Empty for npub.</param>
    public sealed record Nip19DecodeResult(string PubkeyHex, IReadOnlyList<string> RelayHints);

    /// <summary>
    /// Try to decode a NIP-19 entity into a 32-byte public key (and optional relay hints).
    /// Accepts plain bech32 (<c>npub1...</c>, <c>nprofile1...</c>) or the same prefixed with <c>nostr:</c>.
    /// Whitespace is trimmed; case is folded to lower per bech32 spec.
    /// </summary>
    /// <returns><c>true</c> on success; <c>false</c> if the input is malformed or not a pubkey-bearing entity.</returns>
    public static bool TryDecodePubkey(string? input, out Nip19DecodeResult? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var s = input.Trim();
        if (s.StartsWith(NostrUriPrefix, StringComparison.OrdinalIgnoreCase))
        {
            s = s[NostrUriPrefix.Length..];
        }

        s = s.ToLowerInvariant();

        byte[] data;
        string hrp;
        try
        {
            data = Bech32.Decode(s, out hrp);
        }
        catch (FormatException)
        {
            return false;
        }

        switch (hrp)
        {
            case "npub":
                if (data.Length != 32)
                {
                    return false;
                }
                result = new Nip19DecodeResult(Convert.ToHexString(data).ToLowerInvariant(), Array.Empty<string>());
                return true;

            case "nprofile":
                return TryParseNprofileTlv(data, out result);

            default:
                return false;
        }
    }

    /// <summary>
    /// Encode a 32-byte public key + optional relay hints as <c>nprofile1...</c>.
    /// </summary>
    /// <param name="pubkeyHex">64-char hex pubkey.</param>
    /// <param name="relayHints">Relay URLs to embed as TLV type-1 entries. May be null/empty.</param>
    public static string EncodeNprofile(string pubkeyHex, IEnumerable<string>? relayHints = null)
    {
        if (string.IsNullOrWhiteSpace(pubkeyHex))
        {
            throw new ArgumentException("Pubkey hex is required", nameof(pubkeyHex));
        }

        byte[] pubkey;
        try
        {
            pubkey = Convert.FromHexString(pubkeyHex);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("Pubkey hex is not valid hex", nameof(pubkeyHex), ex);
        }

        if (pubkey.Length != 32)
        {
            throw new ArgumentException("Pubkey must be 32 bytes (64 hex chars)", nameof(pubkeyHex));
        }

        var tlv = new List<byte>(2 + 32);
        tlv.Add(TlvTypeSpecial);
        tlv.Add(32);
        tlv.AddRange(pubkey);

        if (relayHints != null)
        {
            foreach (var relay in relayHints)
            {
                if (string.IsNullOrWhiteSpace(relay))
                {
                    continue;
                }

                var bytes = System.Text.Encoding.ASCII.GetBytes(relay);
                if (bytes.Length > 255)
                {
                    // TLV length field is one byte; skip oversized URLs rather than throw.
                    continue;
                }

                tlv.Add(TlvTypeRelay);
                tlv.Add((byte)bytes.Length);
                tlv.AddRange(bytes);
            }
        }

        return Bech32.Encode("nprofile", tlv.ToArray());
    }

    private static bool TryParseNprofileTlv(byte[] data, out Nip19DecodeResult? result)
    {
        result = null;

        string? pubkeyHex = null;
        var relays = new List<string>();

        var i = 0;
        while (i < data.Length)
        {
            // Need at least type + length bytes
            if (i + 2 > data.Length)
            {
                return false;
            }

            var type = data[i];
            var length = data[i + 1];
            var valueStart = i + 2;
            var valueEnd = valueStart + length;

            if (valueEnd > data.Length)
            {
                return false;
            }

            switch (type)
            {
                case TlvTypeSpecial:
                    if (length != 32 || pubkeyHex != null)
                    {
                        return false;
                    }
                    pubkeyHex = Convert.ToHexString(data, valueStart, length).ToLowerInvariant();
                    break;

                case TlvTypeRelay:
                    // Per NIP-19 relay URLs are ASCII; tolerate UTF-8 just in case.
                    var relay = System.Text.Encoding.UTF8.GetString(data, valueStart, length);
                    if (!string.IsNullOrWhiteSpace(relay))
                    {
                        relays.Add(relay);
                    }
                    break;

                default:
                    // Unknown TLV types must be ignored per NIP-19.
                    break;
            }

            i = valueEnd;
        }

        if (pubkeyHex == null)
        {
            return false;
        }

        result = new Nip19DecodeResult(pubkeyHex, relays);
        return true;
    }
}
