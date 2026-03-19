# Amber Phase 2: MLS Event Signing via INostrEventSigner

## Goal
Enable group message sending with Amber by decoupling kind 445 event signing from the local Nostr private key in ManagedMlsService.

## Steps

### Step 5: Create INostrEventSigner abstraction
- [x] Create `src/OpenChat.Core/Services/INostrEventSigner.cs` with interface:
  ```csharp
  Task<byte[]> SignEventAsync(int kind, string content, List<List<string>> tags, string publicKeyHex)
  ```
- [x] Implement `LocalNostrEventSigner` — uses private key directly (extract logic from `BuildSignedNostrEvent`)
- [x] Implement `ExternalNostrEventSigner` — wraps `IExternalSigner.SignEventAsync`, sends unsigned event, parses signed response to bytes

### Step 6: Inject signer into ManagedMlsService
- [x] Add `INostrEventSigner` field to `ManagedMlsService`
- [x] Add `SetNostrEventSigner(INostrEventSigner signer)` method to `IMlsService`, `ManagedMlsService`, and `MlsService` (stub)
- [x] Make `BuildSignedNostrEvent` async → `BuildSignedNostrEventAsync`
- [x] Delegate signing to `_nostrEventSigner.SignEventAsync()` when signer is set; fallback to local signing
- [x] Update `EncryptMessageAsync` (which calls `BuildSignedNostrEvent`) to await the async version
- [x] Wire up in `MainViewModel.InitializeAfterLoginAsync`:
  - Local key login → `LocalNostrEventSigner(privateKeyHex)`
  - Amber login → `ExternalNostrEventSigner(externalSigner)`

## Testing
- [x] Verify all EndToEndChatIntegrationTests still pass (158 passed, 0 failed, 3 skipped)
- [ ] Run real relay integration tests
- [ ] Add test: mock INostrEventSigner verifies signing is delegated correctly

## Files modified
- New: `src/OpenChat.Core/Services/INostrEventSigner.cs`
- `src/OpenChat.Core/Services/ManagedMlsService.cs`
- `src/OpenChat.Core/Services/IMlsService.cs` (add SetNostrEventSigner)
- `src/OpenChat.Core/Services/MlsService.cs` (add SetNostrEventSigner stub)
- `src/OpenChat.Presentation/ViewModels/MainViewModel.cs`

## Status
- [x] Steps 5 & 6 completed
