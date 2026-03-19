# SEC-CRITICAL: Add Event Signature Verification

## Problem
NostrService receives events from relays and processes them (kind 444 Welcome, kind 445 GroupMessage, kind 1059 Gift Wrap) **without verifying the BIP-340 Schnorr signature or event ID hash**. A malicious relay can inject forged events claiming to be from any user.

## Attack Vector
1. Attacker operates a relay (or MITM's a connection to one)
2. Sends forged kind 444 Welcome with arbitrary pubkey → victim joins attacker-controlled group
3. Sends forged kind 445 GroupMessage impersonating another group member
4. Sends forged kind 1059 Gift Wrap with fabricated seal/rumor layers

## Location
- `NostrService.cs:217-262` — `ProcessRelayMessageAsync` processes events without verification
- `NostrService.cs:391-431` — `ParseNostrEvent` extracts fields but never validates id/sig

## Fix
- [x] Add `VerifyEventSignature()` method: recomputes SHA-256 event ID, verifies BIP-340 Schnorr sig
- [x] Uses `CryptographicOperations.FixedTimeEquals` for constant-time ID comparison
- [x] Called inside `ParseNostrEvent` — covers all 3 call sites (live events, welcome fetch, group history fetch)
- [x] Invalid events return null (rejected with warning log)
- [x] Added `Signature` field to `NostrEventReceived` model
- [x] Gift wrap outer signature verified; inner rumor (constructed directly, not via ParseNostrEvent) is unsigned per NIP-59
- [x] Validates field lengths and hex format before crypto operations

## Tests (16 in EventSignatureVerificationTests.cs)
- [x] Valid event passes (plain, with tags, with unicode, kind 1059)
- [x] Tampered content rejected
- [x] Tampered tags rejected
- [x] Forged signature rejected
- [x] Mismatched pubkey rejected
- [x] Tampered event ID rejected
- [x] Tampered timestamp rejected
- [x] Tampered kind rejected
- [x] Malformed input rejected (empty sig, short ID, non-hex ID/pubkey/sig)

## Status
- [x] Complete (148 tests pass, 3 pre-existing relay failures, 3 skipped)
