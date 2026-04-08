# H2 - Validate Incoming Events Match Subscription Filters

**Severity**: HIGH
**Status**: TODO

## Context

When receiving `EVENT` messages from relays, the code verifies signatures but never checks that the event matches the subscription filter that was sent. A malicious relay can push arbitrary events (wrong kind, wrong pubkey, wrong tags) and they'll be processed.

## Files
- `src/OpenChat.Core/Services/NostrService.cs` (lines 342-400, event reception)

## Tasks
- [ ] Track active subscription filters in a `Dictionary<string, NostrFilter>` keyed by subscription ID
- [ ] When receiving an EVENT message, extract the subscription ID (element [1] in the array)
- [ ] Validate: event kind matches filter kinds, event pubkey matches filter authors (if specified), event tags match filter tag constraints
- [ ] If event doesn't match any active filter, log warning and drop it
- [ ] Clean up filter tracking when subscriptions are closed (CLOSE/EOSE)
- [ ] Add tests: matching event accepted, wrong-kind event rejected, event for unknown subscription rejected
