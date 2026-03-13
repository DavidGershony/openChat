# Fix NostrGroupData (0xF2EE) TLS vs QUIC VarInt Encoding Mismatch

## Problem

The Rust MDK and C# MarmotCs both use the `0xF2EE` MLS extension for group metadata (name, description, admins, relays), but they serialize it with **completely different binary formats**. This means neither side can decode the other's extension data.

Currently the C# side catches the `FormatException` and falls back to an empty group name, but this loses metadata.

## Rust MDK Format (TLS Codec)

File: `~/.cargo/git/checkouts/mdk-7d5a3a2420b194f5/d3cb3f1/crates/mdk-core/src/extension/types.rs`

```
u16 (big-endian):     version (currently 2)
[u8; 32]:             nostr_group_id
opaque<2>:            name (2-byte BE length prefix + UTF-8)
opaque<2>:            description
vector<4> of [u8;32]: admin_pubkeys (4-byte BE length prefix, then N*32 bytes)
vector<4> of opaque<2>: relays (4-byte BE length prefix, then len-prefixed strings)
opaque<2>:            image_hash (empty or 32 bytes)
opaque<2>:            image_key (empty or 32 bytes; v2=seed, v1=encryption key)
opaque<2>:            image_nonce (empty or 12 bytes)
opaque<2>:            image_upload_key (v2 only; empty or 32 bytes)
```

- Uses OpenMLS's TLS codec: fixed-width big-endian length prefixes (`opaque<2>` = 2-byte prefix, `vector<4>` = 4-byte prefix)
- Includes `nostr_group_id` (32 bytes) — not present in C#
- Includes 4 image fields — not serialized in C#
- Version field is `u16` big-endian (`0x00 0x02` for version 2)

## C# MarmotCs Format (QUIC VarInt)

File: `C:\Users\david\openCodeProjects\marmot-cs\src\MarmotCs.Protocol\Mip01\NostrGroupDataCodec.cs`

```
varint:               version (1-8 bytes, RFC 9000 encoding)
varint-prefixed:      name (varint length + UTF-8)
varint-prefixed:      description
varint:               num_admins, then num_admins * 32 raw bytes
varint:               num_relays, then varint-prefixed relay URL strings
(no group_id, no image fields)
```

- Uses QUIC VarInt (RFC 9000 Section 16): variable-length integers
- Version field is a single varint byte (`0x02` for version 2)
- Missing `nostr_group_id` field entirely
- Missing all image fields

## Why It Breaks

When Rust creates a group and sends a Welcome, the 0xF2EE extension contains TLS-encoded data starting with `0x00 0x02` (u16 version=2). The C# decoder reads with QUIC VarInt, interprets `0x00` as version 0, then throws:
```
FormatException: Unsupported NostrGroupData version: 0. Expected 2.
```

The reverse direction has the same problem — Rust can't decode C#'s QUIC VarInt format.

## Version History (Rust MDK Changelog)

| Version | MDK Release | Changes |
|---------|-------------|---------|
| 0 | Pre-v0.5.2 | Original, now **rejected** by Rust MDK |
| 1 | v0.5.2 (2025-10-16) | Added version field. Admin pubkeys = hex-encoded strings |
| 2 | v0.6.0 (2026-02-18) | Admin pubkeys = raw 32-byte keys. image_key = seed (HKDF). Added image_upload_key |

C# only implements version 2 because it was built after the v0.6.0 migration.

## Fix Required

The C# codec in `MarmotCs.Protocol` needs to switch from QUIC VarInt to TLS codec to match Rust MDK's format. This is the correct direction since Rust MDK is the reference implementation.

### Changes needed in MarmotCs.Protocol:

1. **`NostrGroupDataCodec.cs`**: Rewrite `Encode()` and `Decode()` to use TLS-style fixed-width big-endian length prefixes instead of QUIC VarInt
2. **`NostrGroupDataExtension.cs`**: Add `NostrGroupId` (byte[32]) property
3. **Add image fields**: `ImageHash`, `ImageKey`, `ImageNonce`, `ImageUploadKey` (all optional byte arrays)
4. **Version field**: Write as `u16` big-endian, not varint
5. **Admin pubkeys**: Write as `vector<4>` of `[u8; 32]` (4-byte BE length prefix)
6. **Relays**: Write as `vector<4>` of `opaque<2>` strings

### Changes needed in MarmotCs.Core:

1. **`Mdk.cs`**: Remove the `FormatException` catch workaround in `PreviewWelcomeAsync` and `AcceptWelcomeAsync` once the codec is fixed
2. **`Mdk.cs`**: Populate `NostrGroupId` when creating groups

### Testing

- Unit tests in `MarmotCs.Protocol.Tests` for round-trip encode/decode
- Cross-encode test: take known Rust-encoded 0xF2EE bytes and verify C# can decode them
- Cross-MDK integration test in openChat: `CrossMdk_RustCreatesGroup_ManagedAcceptsWelcome_ThroughRelay`

## File Reference

| File | Repo | Purpose |
|------|------|---------|
| `NostrGroupDataCodec.cs` | marmot-cs/src/MarmotCs.Protocol/Mip01/ | C# encoder/decoder (needs rewrite) |
| `NostrGroupDataExtension.cs` | marmot-cs/src/MarmotCs.Protocol/Mip01/ | Data model (needs new fields) |
| `types.rs` | mdk-core/src/extension/ (Rust MDK) | Reference implementation |
| `Mdk.cs` | marmot-cs/src/MarmotCs.Core/ | FormatException workaround to remove |
| `CrossMdkRelayIntegrationTests.cs` | openChat/tests/OpenChat.Core.Tests/ | Integration test |
