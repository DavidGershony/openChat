# Migrate KeyPackage events from kind 443 to kind 30443

## Status: Not Started

## Problem

MIP-00 has migrated KeyPackage events from kind 443 (regular) to kind 30443 (addressable). Addressable events auto-replace by `d` tag, eliminating stale KP accumulation on relays. Planned cutover date: 2026-05-01.

## What changes

### Kind 30443 (addressable event)
- Adds a `d` tag with a random 32-byte hex identifier (the "slot")
- Publishing a new KP with the same `d` tag replaces the old one on relays
- No NIP-09 deletion needed for rotation
- Old KPs don't pile up — only the latest per `d` tag exists

### Migration window (now through 2026-05-01)
- **Publishing**: dual-publish kind 443 + kind 30443
- **Fetching**: prefer kind 30443, fall back to kind 443
- **After cutover**: drop kind 443 entirely

## Files to modify
- `src/OpenChat.Core/Services/NostrService.cs` — PublishKeyPackageAsync (dual-publish with `d` tag), FetchKeyPackagesAsync (prefer 30443)
- `src/OpenChat.Core/Services/ManagedMlsService.cs` — store `d` tag value for rotation
- Tests for both kinds
