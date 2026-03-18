# Amber Phase 1: NIP-44 Async + Gift Wrap Signer Support

## Goal
Enable Welcome message send/receive with Amber (NIP-46 remote signer) by adding async NIP-44 encrypt/decrypt that can delegate to the external signer, and making Gift Wrap creation/unwrapping signer-aware.

## Steps

### Step 1: Add async NIP-44 methods to INostrService and NostrService
- [ ] Add `Task<string> Nip44EncryptAsync(string plaintext, string recipientPubKey)` to `INostrService`
- [ ] Add `Task<string> Nip44DecryptAsync(string ciphertext, string senderPubKey)` to `INostrService`
- [ ] Implement in `NostrService`: if `_externalSigner?.IsConnected == true`, delegate to signer; otherwise use local private key
- [ ] Store local private key as field in NostrService (use existing `_subscribedUserPrivKey`)

### Step 2: Make CreateGiftWrap async and signer-aware
- [ ] Rename `CreateGiftWrap` → `CreateGiftWrapAsync`
- [ ] Seal NIP-44 encryption: if signer connected, call `_externalSigner.Nip44EncryptAsync`; otherwise use local crypto
- [ ] Seal signing: if signer connected, call `_externalSigner.SignEventAsync` for the kind 13 seal; otherwise sign locally
- [ ] Outer gift wrap: always use ephemeral key (stays local per NIP-59 spec)
- [ ] Remove `NotSupportedException` in `PublishWelcomeAsync`
- [ ] Update `PublishWelcomeAsync` to call `CreateGiftWrapAsync`

### Step 3: Make UnwrapGiftWrap async and signer-aware
- [ ] Rename `UnwrapGiftWrap` → `UnwrapGiftWrapAsync`
- [ ] If signer connected and no local private key: call `_externalSigner.Nip44DecryptAsync` for both layers (gift wrap → seal, seal → rumor)
- [ ] Otherwise use local crypto as today
- [ ] Update guard condition: allow unwrap when either `_subscribedUserPrivKey != null` OR `_externalSigner?.IsConnected == true`
- [ ] Update callers in `ListenToRelayAsync` and `FetchWelcomeEventsAsync`

### Step 4: Fix SubscribeToWelcomesAsync for signer users
- [ ] Allow subscription even when `privateKeyHex` is null (the REQ doesn't need it, only unwrapping does)
- [ ] Update `MainViewModel.InitializeAfterLoginAsync` to call `SubscribeToWelcomesAsync` for signer users too

## Testing
- [ ] Verify existing local-key tests still pass (run full test suite)
- [ ] Run real relay integration tests to confirm no regression
- [ ] All existing Welcome/invite flows work with local keys

## Files to modify
- `src/OpenChat.Core/Services/INostrService.cs`
- `src/OpenChat.Core/Services/NostrService.cs`
- `src/OpenChat.Presentation/ViewModels/MainViewModel.cs`

## Status
- [ ] Not started
