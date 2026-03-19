# SEC-CRITICAL: Fail-Hard on Key Protection Failure

## Problem
`StorageService.ProtectString()` (line 1192-1208) silently falls back to storing private keys as plaintext in SQLite when DPAPI `Protect()` throws an exception. The user is never notified.

## Attack Vector
1. DPAPI becomes unavailable (user account change, permissions issue, non-Windows platform)
2. Private keys are stored unencrypted in the SQLite database
3. Anyone with file access to the DB can extract private keys

## Location
- `StorageService.cs:1192-1208` — `ProtectString()` catches exception and returns plaintext
- `StorageService.cs:1241-1254` — `ProtectBlob()` returns unencrypted when `_secureStorage == null`

## Fix
- [x] `ProtectString()`: throws `InvalidOperationException` when `_secureStorage` is null (was: silent plaintext fallback)
- [x] `ProtectString()`: lets exceptions propagate when `Protect()` fails (was: catch and return plaintext)
- [x] `ProtectBlob()`: throws `InvalidOperationException` when `_secureStorage` is null (was: return unencrypted)
- [x] `ProtectBlob()`: lets exceptions propagate when `Protect()` fails (was: catch and return unencrypted)
- [x] `UnprotectString()`: lets decrypt failures propagate (was: catch and return encrypted data as plaintext). FormatException still caught for backward-compat plaintext data.
- [x] `UnprotectBlob()`: throws when `_secureStorage` is null (was: return raw data)
- [x] Updated all test files (9 files) to pass `MockSecureStorage` to `StorageService`
- [x] Extracted `MockSecureStorage` to shared test helpers (both Core.Tests and UI.Tests)

## Tests
- [x] `StorageService_WithoutSecureStorage_ThrowsOnPrivateKeySave` — proves plaintext fallback is gone
- [x] `StorageService_WithoutSecureStorage_AllowsNullPrivateKey` — Amber users (no privkey) still work
- [x] All 149 Core tests pass (3 pre-existing relay failures)
- [x] All 94 UI tests pass

## Also fixed (SEC-HIGH #6)
- [x] `ProtectBlob` and `UnprotectBlob` now also fail-hard — this closes the blob encryption fallback issue too

## Status
- [x] Complete
