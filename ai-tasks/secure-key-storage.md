# Secure Key Storage

## Goal
Protect private keys (nsec, PrivateKeyHex, MLS signing keys) at rest instead of storing them as plaintext in SQLite.

## Current State
- `PrivateKeyHex` and `Nsec` stored as plain TEXT in the `Users` table in `openchat.db`
- MLS signing keys and KeyPackage private keys stored as raw bytes in the `MlsState` table (`__service__` blob)
- Anyone with file access can extract the private key

## Requirements
- [ ] Encrypt private keys before writing to SQLite
- [ ] Decrypt on read (transparent to the rest of the app)
- [ ] Use OS-native secure storage where available:
  - **Windows**: DPAPI (`System.Security.Cryptography.ProtectedData`) — encrypts with the user's login credentials
  - **Linux**: libsecret / GNOME Keyring (or fallback to DPAPI-equivalent)
  - **macOS**: Keychain via `Security` framework
  - **Android**: Android Keystore
- [ ] Cross-platform fallback: encrypt with a key derived from a user-provided password (PBKDF2/Argon2)
- [ ] Encrypt MLS service state blob (`__service__` key in MlsState table) — contains signing private key and KeyPackage init/HPKE private keys
- [ ] Migrate existing plaintext keys on first run after upgrade (encrypt in place)
- [ ] Never log private key values (audit existing log statements)

## Technical Notes
- `System.Security.Cryptography.ProtectedData` works on Windows without additional dependencies
- For cross-platform, consider wrapping in an `ISecureStorage` interface with platform-specific implementations
- The `StorageService` is the right place to add encrypt/decrypt — transparent to `MessageService`, `MlsService`, etc.
- Consider encrypting the entire SQLite database with SQLCipher as an alternative approach

## Status
- [ ] Not started
