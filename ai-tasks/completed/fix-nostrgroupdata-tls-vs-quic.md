# Fix NostrGroupData (0xF2EE) Codec: Match Rust MDK Wire Format

## Problem

The C# `NostrGroupDataCodec` uses QUIC VarInt encoding, but the Rust MDK (the reference implementation) uses TLS codec. The C# encoding was written against the MIP-01 field descriptions without verifying the actual Rust binary output. It should have matched the Rust wire format from the start.

The Rust wire format is implicitly defined by `#[derive(TlsSerialize, TlsDeserialize)]` on the `TlsNostrGroupDataExtension` struct in `mdk-core/src/extension/types.rs`. There is no separate wire format spec — the Rust code IS the spec.

Currently a `FormatException` catch in `Mdk.cs` silently drops group metadata on Rust→C# Welcomes. This workaround must be removed once the codec is fixed.

## Rust MDK Wire Format (the source of truth)

File: `~/.cargo/git/checkouts/mdk-7d5a3a2420b194f5/d3cb3f1/crates/mdk-core/src/extension/types.rs`

```
u16 (big-endian):       version (currently 2)
[u8; 32]:               nostr_group_id
opaque<2>:              name (2-byte BE length prefix + UTF-8)
opaque<2>:              description
vector<4> of [u8; 32]:  admin_pubkeys (4-byte BE length prefix, then N*32 bytes)
vector<4> of opaque<2>: relays (4-byte BE length prefix, then len-prefixed strings)
opaque<2>:              image_hash (empty or 32 bytes)
opaque<2>:              image_key (empty or 32 bytes; v2=seed, v1=direct key)
opaque<2>:              image_nonce (empty or 12 bytes)
opaque<2>:              image_upload_key (v2 only; empty or 32 bytes)
```

TLS codec means fixed-width big-endian length prefixes: `opaque<2>` = 2-byte prefix, `vector<4>` = 4-byte prefix. This is NOT QUIC VarInt.

## What C# Currently Does (wrong)

File: `C:\Users\david\openCodeProjects\marmot-cs\src\MarmotCs.Protocol\Mip01\NostrGroupDataCodec.cs`

```
varint:               version
varint-prefixed:      name
varint-prefixed:      description
varint:               num_admins, then num_admins * 32 raw bytes
varint:               num_relays, then varint-prefixed relay URL strings
(no group_id, no image fields)
```

This was built using DotnetMls's `WriteOpaqueV`/`ReadOpaqueV` helpers (QUIC VarInt) because they were convenient. The developer read the MIP-01 field list and picked an encoding that was available in the C# codebase, without checking the Rust binary output.

Three categories of error:
1. **Wrong encoding**: QUIC VarInt instead of TLS codec fixed-width prefixes
2. **Missing fields**: `nostr_group_id` (32 bytes) and 4 image fields not present at all
3. **Wrong version byte**: varint `0x02` vs TLS `0x00 0x02` — causes Rust bytes to be misread as version 0

## Version History (Rust MDK Changelog)

| Version | MDK Release | Changes |
|---------|-------------|---------|
| 0 | Pre-v0.5.2 | Original, now **rejected** by Rust MDK |
| 1 | v0.5.2 (2025-10-16) | Added version field. Admin pubkeys = hex-encoded strings |
| 2 | v0.6.0 (2026-02-18) | Admin pubkeys = raw 32-byte keys. image_key = seed (HKDF). Added image_upload_key |

C# should support version 2 (current) and version 1 (for backward compat with older Rust clients), matching Rust's behavior.

## Fix

Rewrite `NostrGroupDataCodec` to produce and consume the exact same bytes as the Rust `TlsNostrGroupDataExtension`. The Rust struct is the reference — match it field-for-field, byte-for-byte.

### Step 1: Rewrite `NostrGroupDataCodec.cs`

Replace all QUIC VarInt calls with TLS codec equivalents:

| Field | Rust encoding | C# should use |
|-------|--------------|---------------|
| version | `u16` BE | `writer.WriteUint16(version)` |
| nostr_group_id | `[u8; 32]` | `writer.WriteBytes(groupId)` (fixed 32 bytes) |
| name | `opaque<2>` | `writer.WriteUint16((ushort)bytes.Length)` then `writer.WriteBytes(bytes)` |
| description | `opaque<2>` | same as name |
| admin_pubkeys | `vector<4>` of `[u8; 32]` | `writer.WriteUint32((uint)(count * 32))` then each 32-byte key |
| relays | `vector<4>` of `opaque<2>` | `writer.WriteUint32(totalLen)` then each `opaque<2>` string |
| image_hash | `opaque<2>` | 2-byte prefix + 0 or 32 bytes |
| image_key | `opaque<2>` | 2-byte prefix + 0 or 32 bytes |
| image_nonce | `opaque<2>` | 2-byte prefix + 0 or 12 bytes |
| image_upload_key | `opaque<2>` (v2 only) | 2-byte prefix + 0 or 32 bytes |

### Step 2: Update `NostrGroupDataExtension.cs`

Add the missing fields to the data model:
- `NostrGroupId` (byte[32])
- `ImageHash` (byte[]?)
- `ImageKey` (byte[]?)
- `ImageNonce` (byte[]?)
- `ImageUploadKey` (byte[]?)

### Step 3: Update `Mdk.cs` in MarmotCs.Core

- Remove the `try/catch FormatException` workaround in `PreviewWelcomeAsync` and `AcceptWelcomeAsync`
- Populate `NostrGroupId` when creating groups

### Step 4: Verify with a hex dump

Get the Rust MDK to create a group, capture the 0xF2EE extension bytes from the Welcome, and verify the C# codec decodes them correctly. Then encode the same data in C# and verify byte-for-byte equality.

## Testing

1. **Unit test**: Round-trip encode/decode in `MarmotCs.Protocol.Tests`
2. **Cross-encode test**: Hardcode known Rust-encoded 0xF2EE bytes, verify C# decodes them
3. **Cross-MDK integration test**: `CrossMdk_RustCreatesGroup_ManagedAcceptsWelcome_ThroughRelay` in openChat

## File Reference

| File | Repo | Purpose |
|------|------|---------|
| `NostrGroupDataCodec.cs` | marmot-cs/src/MarmotCs.Protocol/Mip01/ | Rewrite: replace QUIC VarInt with TLS codec |
| `NostrGroupDataExtension.cs` | marmot-cs/src/MarmotCs.Protocol/Mip01/ | Add missing fields |
| `types.rs` | mdk-core/src/extension/ (Rust MDK) | The reference — match this exactly |
| `Mdk.cs` | marmot-cs/src/MarmotCs.Core/ | Remove FormatException workaround |
| `CrossMdkRelayIntegrationTests.cs` | openChat/tests/OpenChat.Core.Tests/ | Integration test |
