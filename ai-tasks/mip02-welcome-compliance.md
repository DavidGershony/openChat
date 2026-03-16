# MIP-02 Compliance Audit: Welcome Events (Kind 444)

## Objective

Verify that marmot-cs `MarmotCs.Protocol.Mip02` and OpenChat's `NostrService` correctly implement
MIP-02 (Welcome event creation, NIP-59 gift wrapping, delivery, and processing). Validate
interop with the Rust marmot reference implementation (marmot-web-chat).

## Specification Summary

- **Nostr Kind**: 444 (inner rumor, unsigned)
- **Delivery**: NIP-59 Gift Wrap (kind 1059) — NOT raw kind 444
- **Content**: base64-encoded MLS Welcome message (RFC 9420)
- **Required Tags**:
  - `e`: Nostr event ID of the referenced kind 443 (KeyPackage event)
  - `relays`: relay URLs
  - `encoding`: `"mls-base64"`
- **NIP-59 Wrapping**:
  - Rumor: unsigned kind 444 event with Welcome content and tags
  - Seal: kind 13, NIP-44 encrypted rumor, signed by sender
  - Gift Wrap: kind 1059, NIP-44 encrypted seal, signed by ephemeral key, `p` tag for recipient

## Audit Checklist

### marmot-cs Protocol Layer
- [ ] `WelcomeEventBuilder.BuildWelcomeEvent` produces correct content and tags
- [ ] `encoding` tag value is exactly `"mls-base64"`
- [ ] `e` tag references the correct KeyPackage event ID
- [ ] `WelcomeEventParser.ParseWelcomeEvent` correctly validates encoding
- [ ] Parser extracts KeyPackage event ID and relay list
- [ ] NIP-59 `GiftWrap.SealContent` correctly encrypts using NIP-44
- [ ] NIP-59 `GiftWrap.UnsealContent` correctly decrypts
- [ ] NIP-44 conversation key derivation matches spec (ECDH → HKDF-Extract with salt "nip44-v2")
- [ ] NIP-44 message padding follows spec (next power of 2 chunking)
- [ ] NIP-44 encryption: ChaCha20 + HMAC-SHA256 with correct key derivation

### OpenChat Integration
- [ ] `NostrService.PublishWelcomeAsync` creates unsigned kind 444 rumor (no sig field)
- [ ] Rumor is wrapped in NIP-59 (seal → gift wrap) before publishing
- [ ] Gift wrap uses ephemeral key (different from sender's key)
- [ ] Gift wrap `p` tag targets the correct recipient
- [ ] Gift wrap `created_at` is randomized (+/- up to 2 days) for unlinkability
- [ ] `SubscribeToWelcomesAsync` subscribes to kind 1059 (NOT kind 444)
- [ ] Received kind 1059 events are unwrapped: gift wrap → seal → rumor → kind 444
- [ ] Private key is required for unwrapping (passed to `SubscribeToWelcomesAsync`)
- [ ] `FetchWelcomeEventsAsync` fetches kind 1059 and unwraps before returning
- [ ] Legacy raw kind 444 events are still handled for backward compatibility

### Interop with Rust MDK (marmot-web-chat)
- [ ] C# gift-wrapped Welcome is correctly received and processed by marmot-web-chat
- [ ] Rust gift-wrapped Welcome is correctly received and processed by OpenChat
- [ ] NIP-44 encryption/decryption is compatible between implementations
- [ ] NIP-59 wrapping/unwrapping is compatible between implementations

### NIP-46 External Signer
- [ ] Gift wrap creation with external signer (NIP-46) is either implemented or explicitly unsupported with clear error

## Key Files

- `marmot-cs/src/MarmotCs.Protocol/Mip02/WelcomeEventBuilder.cs`
- `marmot-cs/src/MarmotCs.Protocol/Mip02/WelcomeEventParser.cs`
- `marmot-cs/src/MarmotCs.Protocol/Nip59/GiftWrap.cs`
- `marmot-cs/src/MarmotCs.Protocol/Nip44/Nip44Encryption.cs`
- `openChat/src/OpenChat.Core/Services/NostrService.cs` (PublishWelcomeAsync, CreateGiftWrap, UnwrapGiftWrap)
- `openChat/src/OpenChat.Core/Crypto/Nip44.cs` (OpenChat's NIP-44 implementation)

## Known Issues

- NIP-46 external signer throws `NotSupportedException` for gift wrap (documented, not yet implemented)
- OpenChat has its own NIP-44 implementation (`Crypto/Nip44.cs`) separate from marmot-cs — potential divergence risk

## Test Strategy

1. Unit tests: roundtrip build→wrap→unwrap→parse
2. NIP-44 test vectors: validate encrypt/decrypt against published NIP-44 test vectors
3. Cross-implementation: C# wrap → Rust unwrap (and vice versa)
4. Relay integration: publish gift-wrapped Welcome, verify marmot-web-chat receives invite
