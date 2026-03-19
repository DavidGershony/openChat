# SEC-CRITICAL: NIP-46 Session Secrets Persistence

## Problem
NIP-46 signer session credentials (`SignerSecret`, `SignerLocalPrivateKeyHex`) are persisted in the Users table. If the DB is compromised, an attacker can hijack the signer session and sign events as the user.

## Attack Vector
1. Attacker gains read access to SQLite database file
2. Extracts `SignerSecret` and `SignerLocalPrivateKeyHex`
3. Connects to the same relay with the same credentials
4. Signs events as the victim via their Amber signer

## Location
- `LoginViewModel.cs:264-268` — persists signer session fields
- `User.cs` — `SignerRelayUrl`, `SignerRemotePubKey`, `SignerSecret`, `SignerLocalPrivateKeyHex`, `SignerLocalPublicKeyHex`
- `MainViewModel.cs` — `RestoreSessionAsync` on app restart

## Fix
- [x] Verified: `SignerSecret` and `SignerLocalPrivateKeyHex` already go through `ProtectString()` on save (StorageService.cs:336-337)
- [x] Verified: `UnprotectString()` is used on read (StorageService.cs:1008, 1015)
- [x] With Critical #2 fix (plaintext fallback removal), these secrets are now encrypted or the app refuses to store them
- [x] Added test proving raw DB values are not plaintext

## Tests
- [x] `StorageService_WithSecureStorage_ProtectsSignerFields` — round-trip save/load works
- [x] `StorageService_WithSecureStorage_SignerSecretsNotPlaintextInDb` — raw DB inspection confirms encryption
- [x] `StorageService_WithoutSecureStorage_ThrowsOnPrivateKeySave` — refuses to store without encryption

## Status
- [x] Complete (was already protected; added verification test, confirmed by Critical #2 fix)
