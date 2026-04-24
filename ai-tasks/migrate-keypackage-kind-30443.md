# Migrate KeyPackage events from kind 443 to kind 30443

## Status: Done

## Problem

MIP-00 has migrated KeyPackage events from kind 443 (regular) to kind 30443 (addressable). Addressable events auto-replace by `d` tag, eliminating stale KP accumulation on relays. Planned cutover date: 2026-05-01.

## What changes

### Kind 30443 (addressable event)
- Adds a `d` tag with a random 32-byte hex identifier (the "slot")
- Publishing a new KP with the same `d` tag replaces the old one on relays
- No NIP-09 deletion needed for rotation
- Old KPs don't pile up — only the latest per `d` tag exists

### Migration approach
- Clean switch: kind 443 dropped entirely, kind 30443 only
- No dual-publish needed (NIP-EE already marked "unrecommended")

## Files to modify
- `src/OpenChat.Core/Services/NostrService.cs` — PublishKeyPackageAsync (dual-publish with `d` tag), FetchKeyPackagesAsync (prefer 30443)
- `src/OpenChat.Core/Services/ManagedMlsService.cs` — store `d` tag value for rotation
- Tests for both kinds
