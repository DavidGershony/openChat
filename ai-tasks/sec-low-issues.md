# SEC-LOW: Collected Low-Severity Issues

## Issues

### 15. No WebSocket connection timeout
- **Location:** `NostrService.cs:77`
- **Risk:** Slow/unresponsive relay can hang the app indefinitely
- **Fix:** Add `cts.CancelAfter(TimeSpan.FromSeconds(10))` before connect

### 16. Event ID format not validated
- **Location:** `NostrService.cs:391-431`
- **Risk:** Received event IDs never checked for 64-char hex
- **Fix:** Validate event ID format in ParseNostrEvent

### 17. Uniform timestamp randomization
- **Location:** `NostrService.cs:1093-1104`
- **Risk:** Gift wrap timestamps uniformly distributed in -2 day window, correlation possible
- **Fix:** Use non-uniform distribution or wider window

## Status
- [ ] Not started
