# Amber Phase 1: NIP-44 Async + Gift Wrap Signer Support

## Goal
Enable Welcome message send/receive with Amber (NIP-46 remote signer) by adding async NIP-44 encrypt/decrypt that can delegate to the external signer, and making Gift Wrap creation/unwrapping signer-aware.

## Steps

### Step 1: Add async NIP-44 methods to INostrService and NostrService
- [x] Add `Task<string> Nip44EncryptAsync(string plaintext, string recipientPubKey)` to `INostrService`
- [x] Add `Task<string> Nip44DecryptAsync(string ciphertext, string senderPubKey)` to `INostrService`
- [x] Implement in `NostrService`: if `_externalSigner?.IsConnected == true`, delegate to signer; otherwise use local private key
- [x] Store local private key as field in NostrService (use existing `_subscribedUserPrivKey`)

### Step 2: Make CreateGiftWrap async and signer-aware
- [x] Rename `CreateGiftWrap` → `CreateGiftWrapAsync`
- [x] Seal NIP-44 encryption: if signer connected, call `_externalSigner.Nip44EncryptAsync`; otherwise use local crypto
- [x] Seal signing: if signer connected, call `_externalSigner.SignEventAsync` for the kind 13 seal; otherwise sign locally
- [x] Outer gift wrap: always use ephemeral key (stays local per NIP-59 spec)
- [x] Remove `NotSupportedException` in `PublishWelcomeAsync`
- [x] Update `PublishWelcomeAsync` to call `CreateGiftWrapAsync`

### Step 3: Make UnwrapGiftWrap async and signer-aware
- [x] Rename `UnwrapGiftWrap` → `UnwrapGiftWrapAsync`
- [x] If signer connected and no local private key: call `_externalSigner.Nip44DecryptAsync` for both layers (gift wrap → seal, seal → rumor)
- [x] Otherwise use local crypto as today
- [x] Update guard condition: allow unwrap when either `_subscribedUserPrivKey != null` OR `_externalSigner?.IsConnected == true`
- [x] Update callers in `ListenToRelayAsync` and `FetchWelcomeEventsAsync`

### Step 4: Fix SubscribeToWelcomesAsync for signer users
- [x] Allow subscription even when `privateKeyHex` is null (the REQ doesn't need it, only unwrapping does)
- [x] Update `MainViewModel.InitializeAfterLoginAsync` to call `SubscribeToWelcomesAsync` for signer users too

Note: Step 4 required no code changes. The existing `SubscribeToWelcomesAsync` already accepts null `privateKeyHex`
and sends the REQ using only the public key. `MainViewModel.InitializeAfterLoginAsync` already calls it for
signer users (the guard checks `PublicKeyHex`, not `PrivateKeyHex`). The Step 3 guard condition changes ensure
that incoming gift wraps are unwrapped via the external signer when no local private key is available.

## Testing
- [x] Verify existing local-key tests still pass (run full test suite)
- [ ] Run real relay integration tests to confirm no regression
- [x] All existing Welcome/invite flows work with local keys

## Files modified
- `src/OpenChat.Core/Services/INostrService.cs`
- `src/OpenChat.Core/Services/NostrService.cs`

## Status
- [x] Completed
