# Secure Key Storage

## Goal
Protect private keys (nsec, PrivateKeyHex, MLS signing keys) at rest instead of storing them as plaintext in SQLite.

## Implementation

- [x] Create `ISecureStorage` interface in `OpenChat.Core/Services/`
- [x] Implement `DesktopSecureStorage` (DPAPI) in `OpenChat.UI/Services/`
- [x] Implement `AndroidSecureStorage` (Android Keystore AES-GCM) in `OpenChat.Android/Services/`
- [x] Add helper methods `ProtectString`/`UnprotectString`/`ProtectBlob`/`UnprotectBlob` to `StorageService`
- [x] Protect sensitive User fields on write: `PrivateKeyHex`, `Nsec`, `SignerLocalPrivateKeyHex`, `SignerSecret`
- [x] Unprotect sensitive User fields on read in `ReadUser`
- [x] Protect/unprotect MLS state blob in `SaveMlsStateAsync`/`GetMlsStateAsync`
- [x] Wire `DesktopSecureStorage` into `App.axaml.cs`
- [x] Wire `AndroidSecureStorage` into `MainActivity.cs`
- [x] Add `System.Security.Cryptography.ProtectedData` NuGet package to `OpenChat.UI.csproj`
- [x] Build succeeds, all 158 tests pass

## Architecture Note
`ISecureStorage` placed in `OpenChat.Core/Services/` (not Presentation) because `StorageService` is in Core
and Presentation already references Core (would create circular dependency).

## Magic Prefix
All encrypted data is prefixed with `[0xEE, 0xCC, 0x01, 0x00]` to distinguish from plaintext.
On `Unprotect`: if prefix is missing, return data as-is (backward compat during transition).

## Status
- [x] Complete
