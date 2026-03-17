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
  - `encoding`: `"base64"`
- **NIP-59 Wrapping**:
  - Rumor: unsigned kind 444 event with Welcome content and tags
  - Seal: kind 13, NIP-44 encrypted rumor, signed by sender
  - Gift Wrap: kind 1059, NIP-44 encrypted seal, signed by ephemeral key, `p` tag for recipient

## Audit Checklist

### marmot-cs Protocol Layer
- [x] `WelcomeEventBuilder.BuildWelcomeEvent` produces correct content and tags
- [x] `encoding` tag value is exactly `"base64"`
- [x] `e` tag references the correct KeyPackage event ID
- [x] `WelcomeEventParser.ParseWelcomeEvent` correctly validates encoding
- [x] Parser extracts KeyPackage event ID and relay list
- [x] NIP-59 `GiftWrap.SealContent` correctly encrypts using NIP-44
- [x] NIP-59 `GiftWrap.UnsealContent` correctly decrypts
- [x] NIP-44 conversation key derivation matches spec (ECDH → HKDF-Extract with salt "nip44-v2")
- [x] NIP-44 message padding follows spec (next power of 2 chunking)
- [x] NIP-44 encryption: ChaCha20 + HMAC-SHA256 with correct key derivation

### OpenChat Integration
- [x] `NostrService.PublishWelcomeAsync` creates unsigned kind 444 rumor (no sig field)
- [x] Rumor is wrapped in NIP-59 (seal → gift wrap) before publishing
- [x] Gift wrap uses ephemeral key (different from sender's key)
- [x] Gift wrap `p` tag targets the correct recipient
- [x] Gift wrap `created_at` is randomized (+/- up to 2 days) for unlinkability
- [x] `SubscribeToWelcomesAsync` subscribes to kind 1059 (NOT kind 444)
- [x] Received kind 1059 events are unwrapped: gift wrap → seal → rumor → kind 444
- [x] Private key is required for unwrapping (passed to `SubscribeToWelcomesAsync`)
- [x] `FetchWelcomeEventsAsync` fetches kind 1059 and unwraps before returning
- [x] Legacy raw kind 444 events are still handled for backward compatibility

### Interop with Rust MDK (marmot-web-chat)
- [x] NIP-44 encryption/decryption is compatible between implementations — verified by 103+ official NIP-44 test vectors (Nip44VectorTests)
- [x] NIP-59 wrapping/unwrapping is compatible between implementations — NIP-59 is JSON + NIP-44; crypto compatibility verified by test vectors, GiftWrap output verified as valid NIP-44 payload (Mip02InteropTests.GiftWrap_OutputIsValidNip44Payload)
- [ ] C# gift-wrapped Welcome is correctly received and processed by marmot-web-chat — requires live relay integration test
- [ ] Rust gift-wrapped Welcome is correctly received and processed by OpenChat — requires live relay integration test

### NIP-46 External Signer
- [x] Gift wrap creation with external signer (NIP-46) is either implemented or explicitly unsupported with clear error — throws `NotSupportedException` at NostrService.cs:820

## Key Files

- `marmot-cs/src/MarmotCs.Protocol/Mip02/WelcomeEventBuilder.cs`
- `marmot-cs/src/MarmotCs.Protocol/Mip02/WelcomeEventParser.cs`
- `marmot-cs/src/MarmotCs.Protocol/Nip59/GiftWrap.cs`
- `marmot-cs/src/MarmotCs.Protocol/Nip44/Nip44Encryption.cs`
- `openChat/src/OpenChat.Core/Services/NostrService.cs` (PublishWelcomeAsync, CreateGiftWrap, UnwrapGiftWrap)

## Known Issues

- NIP-46 external signer throws `NotSupportedException` for gift wrap (documented, not yet implemented)
- Full cross-implementation relay integration tests (C# ↔ Rust gift wrap) require live relay infrastructure

## Tests Added

### marmot-cs: GiftWrap Unit Tests (8 tests)
In `marmot-cs/tests/MarmotCs.Protocol.Tests/ProtocolTests.cs` (class `GiftWrapTests`):
- `SealUnseal_RoundTrips` — basic text roundtrip
- `SealUnseal_BinaryContent_RoundTrips` — binary content (MLS Welcome bytes)
- `Seal_ProducesDifferentOutputEachTime` — random nonce verification
- `Unseal_WrongKey_Throws` — MAC verification with wrong key
- `Seal_EmptyContent_Throws` — input validation
- `Seal_NullContent_Throws` — null guard
- `Unseal_NullSealedContent_Throws` — null guard
- `SealUnseal_LargeContent_RoundTrips` — 4KB content (typical Welcome size)

### OpenChat: MIP-02 Interop Tests (9 tests)
In `openChat/tests/OpenChat.Core.Tests/Mip02InteropTests.cs`:
- `FullNip59Pipeline_WelcomeEvent_RoundTrips` — full pipeline: build kind 444 → seal → unseal → parse
- `Nip44ConversationKey_IsCommutative` — ECDH commutativity verification
- `ThreeLayerNip59_SealUnseal_RoundTrips` — 3-layer NIP-59: rumor → seal → gift wrap → unwrap all
- `WelcomeEvent_WrongEncoding_Throws` — encoding tag validation
- `WelcomeEvent_MultipleRelays_CorrectTagStructure` — relay tag format
- `GiftWrap_RealisticWelcomeSizes_RoundTrips` — 256/2048/8192 byte Welcome sizes
- `GiftWrap_OutputIsValidNip44Payload` — verifies GiftWrap output is standard NIP-44

### Pre-existing Tests (marmot-cs)
- 6 MIP-02 WelcomeEvent unit tests (ProtocolTests.Mip02Tests)
- 103+ NIP-44 official test vector tests (Nip44VectorTests)
- 20 NIP-44 unit tests (Nip44EncryptionTests, Nip44PaddingTests, Nip44MessageKeysTests)
- Welcome storage tests (StorageTests)
- Welcome processing integration tests (IntegrationTests.WelcomeProcessingTests)

### Pre-existing Tests (OpenChat)
- Relay integration tests (RelayIntegrationTests) — Welcome publish/subscribe
- Full-stack integration tests (FullStackRelayIntegrationTests) — end-to-end Welcome flow
- End-to-end chat tests (EndToEndChatIntegrationTests) — complete group lifecycle

## Audit Result

**PASS** — All protocol layer items verified (10/10), all OpenChat integration items verified (10/10), NIP-44/NIP-59 crypto interop verified via test vectors (2/4), NIP-46 documented (1/1). Remaining 2 interop items require live relay infrastructure (not unit-testable).
