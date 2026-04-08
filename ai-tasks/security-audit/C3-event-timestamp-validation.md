# C3 - Add Timestamp Validation to Prevent Replay Attacks

**Severity**: CRITICAL
**Status**: COMPLETED

## Context

`NostrService.ParseNostrEvent()` and `VerifyEventSignature()` parse `created_at` but never validate it against current time. A malicious relay can replay old events indefinitely, making stale messages appear as new.

**Exception**: NIP-59 gift wraps (kind 1059) use intentionally randomized timestamps (+/-2.5 days) for privacy. These MUST be excluded from strict timestamp checks.

## Files
- `src/OpenChat.Core/Services/NostrService.cs` (lines 619-720, event parsing/verification)

## Tasks
- [ ] After signature verification, add timestamp bounds check:
  - Reject events with `created_at` more than 15 minutes in the past
  - Reject events with `created_at` more than 5 minutes in the future
- [ ] Exempt kind 1059 (gift wrap) events from timestamp validation (they use randomized timestamps by design)
- [ ] Consider exempting replaceable events (kinds 0, 3, 10002) which are historical by nature
- [ ] Log rejected events at Debug level with reason
- [ ] Add tests: event with old timestamp rejected, fresh timestamp accepted, gift wrap with old timestamp accepted
