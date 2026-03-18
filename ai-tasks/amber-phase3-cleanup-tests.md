# Amber Phase 3: Null Safety, DM Fix, and Test Coverage

## Goal
Clean up all remaining PrivateKeyHex null-safety issues and add test coverage for the full Amber flow.

## Steps

### Step 7: Fix all PrivateKeyHex pass-through sites
- [x] Audit every place `currentUser.PrivateKeyHex` is passed to publish methods
- [x] `ChatListViewModel` — passes to `PublishCommitAsync`, `PublishWelcomeAsync` — verified null is handled by Phase 1/2 changes (all methods accept `string?`)
- [x] `ChatViewModel` — passes `_currentUserPrivateKeyHex` (already nullable `string?`) to `PublishWelcomeAsync` — verified null is handled
- [x] `SettingsViewModel` — passes to `PublishMetadataAsync`, `PublishRelayListAsync`, `PublishKeyPackageAsync` — verified null is handled by `PublishEventAsync` signer branch
- [x] Remove any `!` null-forgiving operators on `PrivateKeyHex` — only one found in MessageService.cs (fixed in Step 8)

### Step 8: Fix DM encryption in MessageService
- [x] `MessageService.SendMessageAsync` calls `EncryptNip44(content, _currentUser.PrivateKeyHex!, ...)` — this NPEs for signer users
- [x] Change to use the async `Nip44EncryptAsync` from Phase 1 (no private key parameter) — delegates to external signer when connected
- [x] Similarly fix `DecryptNip44` calls if any exist in DM receive path — no DecryptNip44 calls found in DM receive path

### Step 9: Fix ChatViewModel.SetUserContext for signer users
- [x] `MainViewModel.InitializeAfterLoginAsync` only calls `SetUserContext` when `PrivateKeyHex` is not empty
- [x] For signer users, `_currentUserPublicKeyHex` in ChatViewModel is never set -> contact resolution fails
- [x] Fixed: `SetUserContext` is now always called (with nullable privateKeyHex), and the method signature updated to accept `string?`

### Step 10: Persist signer session for auto-reconnect on app restart
- [x] Add signer connection fields to `User` model: `SignerRelayUrl`, `SignerRemotePubKey`, `SignerSecret`
- [x] Add columns to Users table in StorageService (with migration for existing DBs)
- [x] Save signer details in `HandleSignerConnectedAsync` (LoginViewModel) after successful Amber login
- [x] On app restart in `InitializeAfterLoginAsync`: if user has no `PrivateKeyHex` but has `SignerRelayUrl`+`SignerRemotePubKey`+`SignerSecret`, auto-reconnect via `ExternalSignerService.ConnectWithStringAsync` before proceeding
- [x] Wire the reconnected signer into `NostrService.SetExternalSigner` and `MlsService.SetNostrEventSigner`
- [x] Handle reconnection failure gracefully (show status message in ChatListViewModel)
- [x] Amber remembers the authorization -- no re-approval prompt needed on reconnect
- [x] Added `RelayUrl`, `RemotePubKey`, `Secret` public properties to `IExternalSigner` and `ExternalSignerService`

### Step 11: Test coverage
- [ ] Add unit tests with mock `IExternalSigner`:
  - `PublishEventAsync` with null privkey + signer -> calls `SignEventAsync`
  - `CreateGiftWrapAsync` with signer -> calls `Nip44EncryptAsync` for seal + `SignEventAsync`
  - `UnwrapGiftWrapAsync` with signer -> calls `Nip44DecryptAsync` twice
  - `EncryptMessageAsync` with `ExternalNostrEventSigner` -> produces valid signed event
- [ ] Add real relay integration test: full round-trip with mock signer
- [ ] Verify ALL existing tests pass -- zero regressions

## Files modified
- `src/OpenChat.Core/Models/User.cs` (added signer session fields)
- `src/OpenChat.Core/Services/StorageService.cs` (migration + save/read signer fields)
- `src/OpenChat.Core/Services/MessageService.cs` (DM encryption fix: EncryptNip44 -> Nip44EncryptAsync)
- `src/OpenChat.Core/Services/IExternalSigner.cs` (added RelayUrl, RemotePubKey, Secret properties)
- `src/OpenChat.Core/Services/ExternalSignerService.cs` (exposed session properties)
- `src/OpenChat.Presentation/ViewModels/LoginViewModel.cs` (persist signer session on connect)
- `src/OpenChat.Presentation/ViewModels/MainViewModel.cs` (auto-reconnect on restart, always set user context)
- `src/OpenChat.Presentation/ViewModels/ChatViewModel.cs` (SetUserContext accepts nullable privateKeyHex)

## Status
- [x] Steps 7-10 completed (all 158 tests pass, 0 failures)
- [ ] Step 11 not started (test coverage)
