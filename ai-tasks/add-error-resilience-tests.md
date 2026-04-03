# Add error resilience and adversarial tests

## Status: Not Started

## Problem

The test suite has no coverage for error recovery, network failures, or adversarial scenarios. Real-world usage involves relay disconnections, malformed events, and potentially malicious actors.

## What to test

### Relay resilience
- [ ] Relay disconnect during message send → error surfaced, message marked failed
- [ ] Relay reconnect → subscriptions restored
- [ ] All relays down → graceful degradation, not crash

### MLS state recovery
- [ ] Process welcome with wrong KeyPackage → error logged, other KPs still tried
- [ ] Decrypt with stale epoch → appropriate error (not crash)
- [ ] Group state missing from DB → logged, group marked as needing repair

### Adversarial / negative
- [ ] Replayed kind 445 event (same event ID) → ignored
- [ ] Event with forged signature → rejected before decryption
- [ ] Gift wrap addressed to wrong pubkey → ignored
- [ ] Malformed JSON in kind 0 metadata → logged, avatar not updated (not crash)
- [ ] Oversized message content → handled gracefully

### Timeout handling
- [ ] Relay connection timeout → error status emitted
- [ ] Metadata fetch timeout → returns null, not hang
- [ ] MLS operation timeout → does not block UI thread
