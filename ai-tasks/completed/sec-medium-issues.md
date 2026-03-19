# SEC-MEDIUM: Collected Medium-Severity Issues

## Issues

### 9. Timing side-channel on KeyPackage matching
- **Location:** `ManagedMlsService ProcessWelcomeAsync`
- **Risk:** Sequential KP attempts leak count and index via response timing
- **Fix:** Add constant-time padding or randomize attempt order

### 10. QR code contains NIP-46 secret
- **Location:** `LoginViewModel.cs:148-150`
- **Risk:** Screenshots or screen recordings leak the `nostrconnect://` URI secret
- **Fix:** Display warning about screenshot risk, or use out-of-band secret exchange

### 11. Crypto exception details logged
- **Location:** `NostrService.cs:1030`
- **Risk:** Full exception on gift wrap unwrap failure could leak crypto internals
- **Fix:** Log only exception type for crypto operations, not full message/stack

### 12. Signer session details logged
- **Location:** `LoginViewModel.cs:271-272`
- **Risk:** Full relay URL and remote pubkey in logs
- **Fix:** Truncate or omit sensitive details from log messages

### 13. Unvalidated tags on received events
- **Location:** `NostrService.cs ParseNostrEvent`
- **Risk:** Malformed tags from malicious relay could cause parsing issues
- **Fix:** Validate tag structure (non-empty, reasonable length) before processing

### 14. Single hardcoded relay for signer
- **Location:** `LoginViewModel.cs:148`
- **Risk:** Single point of failure for Amber connections
- **Fix:** Allow user-configurable signer relay, provide fallback list

## Status
- [x] All 6 issues fixed, builds clean, 145 Core + 94 UI tests pass
