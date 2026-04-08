# H2 - Validate Incoming Events Match Subscription Filters

**Severity**: HIGH
**Status**: TODO — blocked on NostrRelayConnection wrapper

## Context

When receiving `EVENT` messages from relays, the code verifies signatures but never checks
that the event matches the subscription filter that was sent. A malicious relay can push
arbitrary events (wrong kind, wrong pubkey, wrong tags) and they'll be processed.

## Dependency

This fix requires centralized subscription tracking — knowing which `subId` maps to which
filter. Currently there are 11 ad-hoc REQ sites that build filters inline with no tracking.

The `NostrRelayConnection` wrapper task (`ai-tasks/blockcore-nostr-client.md`) creates this
tracking infrastructure as part of auto-reconnect (must replay subscriptions). Once that's
done, H2 validation is a small addition: extract `root[1]` from EVENT messages, look up
the subscription, check event kind matches.

**Do the wrapper task first, then H2 comes almost for free.**

## Files
- `src/OpenChat.Core/Services/NostrService.cs` (lines 342-400, event reception)

## Tasks
- [ ] Prerequisite: Complete NostrRelayConnection wrapper with subscription tracking
- [ ] In `ProcessRelayMessageAsync`, extract subscription ID from `root[1]`
- [ ] Look up registered filter for that subscription ID
- [ ] Validate: event kind matches filter kinds
- [ ] If event doesn't match, log warning and drop it
- [ ] Clean up filter tracking when subscriptions are closed (CLOSE/EOSE)
- [ ] Add tests: matching event accepted, wrong-kind event rejected, unknown subscription rejected
