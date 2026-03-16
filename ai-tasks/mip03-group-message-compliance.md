# MIP-03 Compliance Audit: Group Commit/Message Events (Kind 445)

## Objective

Verify that marmot-cs `MarmotCs.Protocol.Mip03` and OpenChat correctly implement MIP-03
(group message encryption, publishing, receiving, and commit race resolution). Validate
interop with the Rust marmot reference implementation.

## Specification Summary

- **Nostr Kind**: 445
- **Content Encryption**: ChaCha20-Poly1305 (AEAD)
- **Encryption Key**: MLS exporter secret via `MLS-Exporter("marmot", "group-event", 32)`
- **Content Format**: `base64(nonce[12] || ciphertext || auth_tag[16])`
- **AAD**: Empty byte string
- **Required Tags**:
  - `h`: Group ID (32-byte Nostr group ID in hex, from 0xF2EE extension)
  - `encoding`: `"base64"`
- **Commit Race Resolution**:
  1. Earliest `created_at` wins
  2. Tie-break: smallest event ID (lexicographic hex)

## Audit Checklist

### marmot-cs Protocol Layer
- [ ] `GroupEventBuilder.BuildGroupEvent` encrypts with ChaCha20-Poly1305, random 12-byte nonce
- [ ] Output format is `base64(nonce || ciphertext || tag)` — nonce first, tag last
- [ ] AAD is empty (not null, not group ID — empty)
- [ ] `h` tag contains group ID as lowercase hex
- [ ] `encoding` tag is `"base64"` (NOT `"mls-base64"` — different from MIP-00/02)
- [ ] `GroupEventParser.ParseGroupEvent` correctly extracts nonce, ciphertext, tag
- [ ] Parser validates minimum payload size (29 bytes: 12 nonce + 16 tag + 1 ciphertext)
- [ ] Decryption key is exactly 32 bytes
- [ ] `GroupEventEncryption` matches `GroupEventBuilder`/`GroupEventParser` (no divergence)

### Encryption Key Derivation
- [ ] Key derived via `MLS-Exporter("marmot", "group-event", 32)` — verify label and context
- [ ] Key is re-derived each epoch (not cached across epoch transitions)
- [ ] `ExporterSecretKeyDerivation` in marmot-cs matches this derivation
- [ ] `Mip03Crypto` in marmot-cs core correctly calls exporter

### Commit Race Resolution
- [ ] `CommitRaceResolver.ResolveWinner` picks earliest `created_at`
- [ ] Tie-break uses lexicographic comparison on event ID hex
- [ ] Event ID comparison should be case-sensitive (hex is lowercase by convention)
- [ ] Single-commit case returns immediately without comparison

### OpenChat Integration
- [ ] `MessageService` subscribes to kind 445 events for joined groups
- [ ] Received events are decrypted using current epoch's exporter key
- [ ] Failed decryption (wrong epoch, corrupted) is handled gracefully
- [ ] `PublishGroupMessageAsync` encrypts and publishes kind 445 correctly
- [ ] Group ID in `h` tag matches the 0xF2EE extension's `nostr_group_id`
- [ ] Commit race resolution is applied when multiple commits arrive for same epoch

### Interop with Rust MDK
- [ ] C# encrypted kind 445 events are correctly decrypted by Rust
- [ ] Rust encrypted kind 445 events are correctly decrypted by C#
- [ ] Exporter key derivation matches between implementations
- [ ] Commit race resolution produces same winner in both implementations

## Key Files

- `marmot-cs/src/MarmotCs.Protocol/Mip03/GroupEventBuilder.cs`
- `marmot-cs/src/MarmotCs.Protocol/Mip03/GroupEventParser.cs`
- `marmot-cs/src/MarmotCs.Protocol/Mip03/CommitRaceResolver.cs`
- `marmot-cs/src/MarmotCs.Protocol/Crypto/GroupEventEncryption.cs`
- `marmot-cs/src/MarmotCs.Protocol/Crypto/ExporterSecretKeyDerivation.cs`
- `marmot-cs/src/MarmotCs.Core/Mip03Crypto.cs`
- `openChat/src/OpenChat.Core/Services/MessageService.cs`
- `openChat/src/OpenChat.Core/Services/NostrService.cs` (PublishGroupMessageAsync)

## Potential Issues Identified

- `CommitRaceResolver` uses `StringComparison.OrdinalIgnoreCase` for event ID comparison — should be `Ordinal` since hex event IDs are lowercase by convention
- `GroupEventEncryption.Decrypt` has misleading comments about minimum payload size

## Test Strategy

1. Unit tests: roundtrip encrypt→decrypt with known key
2. Cross-MDK test: encrypt in C#, decrypt in Rust (and vice versa)
3. Commit race test: verify same winner for identical inputs in both implementations
4. Epoch transition test: verify key rotation produces correct new exporter key
5. Relay integration: send messages between C# and Rust clients via real relay
