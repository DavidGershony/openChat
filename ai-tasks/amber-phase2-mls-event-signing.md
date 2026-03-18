# Amber Phase 2: MLS Event Signing via INostrEventSigner

## Goal
Enable group message sending with Amber by decoupling kind 445 event signing from the local Nostr private key in ManagedMlsService.

## Steps

### Step 5: Create INostrEventSigner abstraction
- [ ] Create `src/OpenChat.Core/Services/INostrEventSigner.cs` with interface:
  ```csharp
  Task<byte[]> SignEventAsync(int kind, string content, List<List<string>> tags, string publicKeyHex)
  ```
- [ ] Implement `LocalNostrEventSigner` — uses private key directly (extract logic from `BuildSignedNostrEvent`)
- [ ] Implement `ExternalNostrEventSigner` — wraps `IExternalSigner.SignEventAsync`, sends unsigned event, parses signed response to bytes

### Step 6: Inject signer into ManagedMlsService
- [ ] Add `INostrEventSigner` field to `ManagedMlsService`
- [ ] Add `SetNostrEventSigner(INostrEventSigner signer)` method
- [ ] Make `BuildSignedNostrEvent` async → `BuildSignedNostrEventAsync`
- [ ] Delegate signing to `_nostrEventSigner.SignEventAsync()` instead of local `SignSchnorr`
- [ ] Update `EncryptMessageAsync` (which calls `BuildSignedNostrEvent`) to await the async version
- [ ] Wire up in `MainViewModel.InitializeAfterLoginAsync`:
  - Local key login → `LocalNostrEventSigner(privateKeyHex)`
  - Amber login → `ExternalNostrEventSigner(externalSigner)`

## Testing
- [ ] Verify `EncryptedEvent_HasCorrectNostrGroupId` test still passes
- [ ] Verify all EndToEndChatIntegrationTests still pass
- [ ] Run real relay integration tests
- [ ] Add test: mock INostrEventSigner verifies signing is delegated correctly

## Files to modify
- New: `src/OpenChat.Core/Services/INostrEventSigner.cs`
- `src/OpenChat.Core/Services/ManagedMlsService.cs`
- `src/OpenChat.Core/Services/IMlsService.cs` (add SetNostrEventSigner)
- `src/OpenChat.Presentation/ViewModels/MainViewModel.cs`

## Status
- [ ] Not started
