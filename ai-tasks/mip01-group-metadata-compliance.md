# MIP-01 Compliance Audit: Group Metadata Extension (0xF2EE)

## Objective

Verify that marmot-cs `MarmotCs.Protocol.Mip01` correctly implements the Nostr group metadata
MLS extension, and that OpenChat correctly creates and processes groups with this extension.
Validate interop with the Rust marmot reference implementation.

## Specification Summary

- **MLS Extension Type**: `0xF2EE`
- **Purpose**: Encodes Nostr group metadata into MLS group state
- **Transported In**: Welcome messages (kind 444) and group commits (kind 445)
- **Wire Format** (TLS codec, u16 big-endian length prefixes):
  ```
  u16           version           (current: 2)
  [u8; 32]      nostr_group_id
  opaque<2>     name              (UTF-8)
  opaque<2>     description       (UTF-8)
  vector<2>     admin_pubkeys     (concatenated 32-byte keys)
  vector<2>     relays            (each as opaque<2>, UTF-8)
  opaque<2>     image_hash        (0 or 32 bytes)
  opaque<2>     image_key         (0 or 32 bytes)
  opaque<2>     image_nonce       (0 or 12 bytes)
  opaque<2>     image_upload_key  (0 or 32 bytes, v2+ only)
  ```

## Audit Checklist

### marmot-cs Protocol Layer
- [ ] `NostrGroupDataCodec.Encode` produces correct TLS wire format (u16 big-endian, NOT QUIC VarInt)
- [ ] All field lengths are u16 big-endian (known prior bug: was using QUIC VarInt)
- [ ] `nostr_group_id` is exactly 32 bytes
- [ ] `admin_pubkeys` length is divisible by 32
- [ ] Relays vector correctly nests per-entry length prefixes inside outer vector length
- [ ] Version 2 includes `image_upload_key`; version 1 omits it
- [ ] `NostrGroupDataCodec.Decode` correctly reads all fields
- [ ] Decode handles version 1 data (no `image_upload_key`) gracefully
- [ ] Roundtrip encode→decode preserves all fields exactly

### OpenChat Integration
- [ ] `ManagedMlsService.CreateGroupAsync` includes 0xF2EE extension with correct group metadata
- [ ] Group name, description, relays are populated from UI input
- [ ] Admin pubkeys contain the creating user's public key
- [ ] `ProcessWelcomeAsync` correctly extracts group metadata from the Welcome's extension
- [ ] Group metadata is persisted to storage after join

### Interop with Rust MDK
- [ ] C# group with 0xF2EE extension → Rust processes Welcome successfully
- [ ] Rust group with 0xF2EE extension → C# processes Welcome successfully
- [ ] Wire format byte-for-byte comparison between implementations
- [ ] Image encryption key derivation matches (HKDF from MLS exporter secret)

## Key Files

- `marmot-cs/src/MarmotCs.Protocol/Mip01/NostrGroupDataCodec.cs`
- `marmot-cs/src/MarmotCs.Protocol/Mip01/NostrGroupDataExtension.cs`
- `marmot-cs/src/MarmotCs.Protocol/Crypto/ImageEncryption.cs`
- `marmot-cs/src/MarmotCs.Protocol/Crypto/ExporterSecretKeyDerivation.cs`
- `openChat/src/OpenChat.Core/Services/ManagedMlsService.cs`
- `marmot-cs/tests/MarmotCs.Protocol.Tests/ProtocolTests.cs`

## Test Strategy

1. Unit tests: roundtrip encode→decode with all field combinations
2. Wire format test: compare C# encoded bytes against Rust encoded bytes for identical input
3. Cross-MDK test: create group in C#, join from Rust (and vice versa)
