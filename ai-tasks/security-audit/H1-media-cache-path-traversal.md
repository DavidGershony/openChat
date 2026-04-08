# H1 - Fix Path Traversal in MediaCacheService

**Severity**: HIGH
**Status**: COMPLETED

## Context

`MediaCacheService.GetCachePath()` at line 79 builds file paths using `messageId` directly:
```csharp
return Path.Combine(_cacheDir, $"{messageId}{ext}");
```

`messageId` comes from Nostr events received from relays (untrusted input). A crafted `messageId` like `../../../sensitive/file` could read/write files outside the cache directory.

## Files
- `src/OpenChat.Core/Services/MediaCacheService.cs` (lines 66-80)

## Tasks
- [ ] Validate `messageId` matches Nostr event ID format: `^[a-f0-9]{64}$`
- [ ] If validation fails, log warning and return null (GetCached) or skip (Save)
- [ ] As defense-in-depth: after `Path.Combine`, verify with `Path.GetFullPath()` that result starts with `_cacheDir`
- [ ] Add tests: valid 64-char hex ID works, path traversal `../` rejected, empty string rejected, non-hex chars rejected
