# MIP-01 Compliance Audit: Group Metadata Extension (0xF2EE)

## Objective

Verify that marmot-cs `MarmotCs.Protocol.Mip01` correctly implements the Nostr group metadata
MLS extension, and that OpenChat correctly creates and processes groups with this extension.
Validate interop with the Rust marmot reference implementation.

## Specification Summary

- **MLS Extension Type**: `0xF2EE`
- **Purpose**: Encodes Nostr group metadata into MLS group state
- **Transported In**: Welcome messages (kind 444) and group commits (kind 445)
- **Wire Format** (TLS codec, MLS VarInt length prefixes per RFC 9000 Section 16):
  ```
  u16           version           (current: 2)
  [u8; 32]      nostr_group_id
  opaque<V>     name              (UTF-8)
  opaque<V>     description       (UTF-8)
  vector<V>     admin_pubkeys     (concatenated 32-byte keys)
  vector<V>     relays            (each as opaque<V>, UTF-8)
  opaque<V>     image_hash        (0 or 32 bytes)
  opaque<V>     image_key         (0 or 32 bytes)
  opaque<V>     image_nonce       (0 or 12 bytes)
  opaque<V>     image_upload_key  (0 or 32 bytes, v2+ only)
  ```

## Audit Checklist

### marmot-cs Protocol Layer
- [x] `NostrGroupDataCodec.Encode` produces correct TLS wire format (MLS VarInt)
- [x] All variable-length fields use MLS VarInt (QUIC-style) length prefixes matching Rust MDK
- [x] `nostr_group_id` is exactly 32 bytes
- [x] `admin_pubkeys` length is divisible by 32
- [x] Relays vector correctly nests per-entry length prefixes inside outer vector length
- [x] Version 2 includes `image_upload_key`; version 1 omits it
- [x] `NostrGroupDataCodec.Decode` correctly reads all fields
- [x] Decode handles version 1 data (no `image_upload_key`) gracefully
- [x] Roundtrip encode→decode preserves all fields exactly

### OpenChat Integration
- [x] `ManagedMlsService.CreateGroupAsync` includes 0xF2EE extension with correct group metadata
- [x] Group name is populated from function parameter
- [x] Admin pubkeys contain the creating user's public key
- [x] `ProcessWelcomeAsync` correctly extracts group metadata from the Welcome's extension
- [ ] Group metadata is persisted to storage after join (not yet verified — storage layer test needed)

### Interop with Rust MDK
- [ ] C# group with 0xF2EE extension → Rust processes Welcome successfully (known dotnet-mls issue: key schedule/ratchet tree)
- [x] Rust group with 0xF2EE extension → C# processes Welcome successfully (Test 1: RustGroup_CSharpProcessesWelcome_ExtractsGroupMetadata)
- [x] Wire format VarInt encoding matches between implementations (verified by successful cross-MDK decode)
- [ ] Image encryption key derivation matches (HKDF from MLS exporter secret) — not yet tested

## Bug Found & Fixed

**NostrGroupDataCodec used u16 big-endian length prefixes instead of MLS VarInt**: The Rust MDK
uses QUIC-style VarInt (RFC 9000 Section 16) for all `<V>` fields in the 0xF2EE extension data.
The C# codec was incorrectly using fixed u16 big-endian length prefixes. This caused a decode
failure when processing Rust-created Welcomes (C# read `0x1152` = 4434 as a u16 length prefix,
when the actual VarInt encoding was `0x11` = 17 for "Rust MIP-01 Group"). Fixed by switching
`Encode`/`Decode` to use `WriteOpaqueV`/`ReadOpaqueV`/`WriteVectorV`/`ReadVectorV`.

## Key Files

- `marmot-cs/src/MarmotCs.Protocol/Mip01/NostrGroupDataCodec.cs`
- `marmot-cs/src/MarmotCs.Protocol/Mip01/NostrGroupDataExtension.cs`
- `openChat/src/OpenChat.Core/Services/ManagedMlsService.cs`
- `marmot-cs/tests/MarmotCs.Protocol.Tests/ProtocolTests.cs`
- `openChat/tests/OpenChat.Core.Tests/Mip01InteropTests.cs`

## Test Results

### Unit Tests (marmot-cs, 14 passing)
All `Mip01Tests` in `ProtocolTests.cs` pass, including:
- Roundtrip encode/decode with empty fields, Unicode, image fields
- Version 1/2 handling
- Validation (32-byte group ID, admin key alignment)
- Extension type 0xF2EE constant
- Wire format verification (VarInt prefixes)

### Interop Tests (openChat, 3 passing + 1 skipped)
- `RustGroup_CSharpProcessesWelcome_ExtractsGroupMetadata` — **PASS**: Rust creates group, C# processes Welcome, extracts correct group name from 0xF2EE
- `NostrGroupDataCodec_WireFormat_VarIntPrefixes` — **PASS**: Verifies VarInt field positions
- `NostrGroupDataExtension_ProducesCorrectExtensionType` — **PASS**: Extension type 0xF2EE constant and roundtrip
- `CSharpGroup_RustProcessesWelcome` — **SKIPPED**: Known dotnet-mls issue (C# Welcome bytes not compatible with Rust MDK)
