# SEC-HIGH: Unencrypted Blob Storage Fallback

## Problem
`StorageService.ProtectBlob()` returns unencrypted data when `_secureStorage == null`. MLS state blobs containing group keys can be stored in plaintext.

## Location
- `StorageService.cs:1241-1254` — `ProtectBlob()`

## Fix
- [ ] Throw when `_secureStorage` is null and blob protection is requested
- [ ] Same treatment for `UnprotectBlob()`
- [ ] Review callers for graceful error handling

## Tests Required
- [ ] `ProtectBlob` throws when `_secureStorage` is null
- [ ] `UnprotectBlob` throws when `_secureStorage` is null

## Status
- [ ] Not started
