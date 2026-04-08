# M2 - Encrypt or Auto-Expire Media Cache

**Severity**: MEDIUM
**Status**: TODO

## Context

Decrypted MIP-04 media (images, audio) is written as plaintext files to `{AppData}/media/`. These files persist indefinitely with no encryption and no expiry. Anyone with access to the user's profile directory can read all decrypted media.

## Files
- `src/OpenChat.Core/Services/MediaCacheService.cs`

## Tasks
- [ ] Option A: Encrypt cached files using the same ISecureStorage mechanism used for DB fields
- [ ] Option B (simpler): Implement cache expiry -- delete files older than N days on startup
- [ ] Add a cache size limit (e.g. 500MB) with LRU eviction
- [ ] Log cache cleanup stats at Info level on startup
