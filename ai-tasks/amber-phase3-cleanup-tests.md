# Amber Phase 3: Null Safety, DM Fix, and Test Coverage

## Goal
Clean up all remaining PrivateKeyHex null-safety issues and add test coverage for the full Amber flow.

## Steps

### Step 7: Fix all PrivateKeyHex pass-through sites
- [ ] Audit every place `currentUser.PrivateKeyHex` is passed to publish methods
- [ ] `ChatListViewModel` — passes to `PublishCommitAsync`, `PublishWelcomeAsync` — verify null is handled by Phase 1/2 changes
- [ ] `ChatViewModel` — passes to publish methods — verify null is handled
- [ ] `SettingsViewModel` — passes to `PublishMetadataAsync`, `PublishRelayListAsync`, `PublishKeyPackageAsync` — verify null is handled by `PublishEventAsync` signer branch
- [ ] Remove any `!` null-forgiving operators on `PrivateKeyHex`

### Step 8: Fix DM encryption in MessageService
- [ ] `MessageService.SendMessageAsync` calls `EncryptNip44(content, _currentUser.PrivateKeyHex!, ...)` — this NPEs for signer users
- [ ] Change to use the async `Nip44EncryptAsync` from Phase 1 (no private key parameter)
- [ ] Similarly fix `DecryptNip44` calls if any exist in DM receive path

### Step 9: Fix ChatViewModel.SetUserContext for signer users
- [ ] `MainViewModel.InitializeAfterLoginAsync` only calls `SetUserContext` when `PrivateKeyHex` is not empty
- [ ] For signer users, `_currentUserPublicKeyHex` in ChatViewModel is never set → contact resolution fails
- [ ] Add `SetUserContext` call for signer users with public key only

### Step 10: Test coverage
- [ ] Add unit tests with mock `IExternalSigner`:
  - `PublishEventAsync` with null privkey + signer → calls `SignEventAsync`
  - `CreateGiftWrapAsync` with signer → calls `Nip44EncryptAsync` for seal + `SignEventAsync`
  - `UnwrapGiftWrapAsync` with signer → calls `Nip44DecryptAsync` twice
  - `EncryptMessageAsync` with `ExternalNostrEventSigner` → produces valid signed event
- [ ] Add real relay integration test: full round-trip with mock signer
- [ ] Verify ALL existing tests pass — zero regressions

## Files to modify
- `src/OpenChat.Core/Services/MessageService.cs`
- `src/OpenChat.Presentation/ViewModels/MainViewModel.cs`
- `src/OpenChat.Presentation/ViewModels/ChatViewModel.cs`
- `tests/OpenChat.Core.Tests/` (new test file)
- `tests/OpenChat.UI.Tests/` (update mocks)

## Status
- [ ] Not started
