# H4 - Zero Key Material from Memory After Use

**Severity**: HIGH
**Status**: TODO

## Context

Private keys and ephemeral key material remain in memory as plain `string`/`byte[]` indefinitely. They're never explicitly cleared, surviving in memory until GC non-deterministically collects them -- and may be paged to disk swap.

Key locations:
- `NostrService._subscribedUserPrivKey` (string, line 47) -- lives for entire session
- `ExternalSignerService` ephemeral keys (line 928) -- never zeroed after ECDH
- `ManagedMlsService._signingPrivateKey` (byte[], line 33) -- session lifetime
- `ManagedMlsService._storedKeyPackages[].InitPrivateKey/HpkePrivateKey` -- until consumed

## Files
- `src/OpenChat.Core/Services/NostrService.cs`
- `src/OpenChat.Core/Services/ExternalSignerService.cs`
- `src/OpenChat.Core/Services/ManagedMlsService.cs`

## Tasks
- [ ] For `byte[]` keys: call `CryptographicOperations.ZeroMemory(span)` after use in all crypto operations
- [ ] For ephemeral keys in ExternalSignerService: zero immediately after ECDH computation
- [ ] On logout/dispose: zero `_signingPrivateKey` and all stored KeyPackage private material
- [ ] For `string` private keys: this is harder (.NET strings are immutable/interned). Consider storing as `byte[]` instead and clearing on dispose. Document the limitation if string storage is kept.
- [ ] Add `IDisposable` to services that hold key material, with explicit clearing in `Dispose()`
