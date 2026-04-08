# M3 - Add Rate Limiting for Relay Event Processing

**Severity**: MEDIUM
**Status**: COMPLETED

## Context

No per-relay rate limiting exists for incoming events. A malicious relay can flood millions of small valid events, exhausting memory and CPU (denial of service). The deduplication cache (10K entries) helps but doesn't prevent resource exhaustion from unique events.

## Files
- `src/OpenChat.Core/Services/NostrService.cs` (lines 625-649, event processing)

## Tasks
- [ ] Add per-relay event rate counter (e.g. sliding window of 60 seconds)
- [ ] If a relay exceeds threshold (e.g. 500 events/minute), log warning and drop events from that relay for a cooldown period
- [ ] Consider disconnecting from relays that consistently exceed limits
- [ ] Add configurable threshold via ProfileConfiguration
