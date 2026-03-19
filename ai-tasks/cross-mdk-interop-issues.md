# Cross-MDK Interop Issues (OpenChat ↔ Marmot Web)

## Issue 1: Web client crashes when inviting OpenChat user
- **Error:** `Uncaught TypeError: can't access property "slice", o.id is undefined`
- **Where:** Marmot-ts web client, minified code, during commit/invite flow
- **Trigger:** Web user tries to invite OpenChat user to a group
- **Key package on relay:** Verified valid — has id, pubkey, sig, all MIP-00 tags (verified by pulling from wss://relay.angor.io)
- **Root cause:** Likely a marmot-ts bug — event object missing `id` in the ingest/commit processing path. The `.id.slice(0,8)` pattern is used extensively in marmot-group.ts for logging.
- **Action:** Report to marmot-protocol/marmot-ts GitHub

## Issue 2: Web client can't decrypt OpenChat messages (OperationError)
- **Error:** `OperationError: The operation failed for an operation-specific reason` (repeated)
- **Event:** 71539c7da157dfea (kind 445 from OpenChat, verified on relay)
- **Analysis:**
  - MIP-03 format matches: nonce[12] || ciphertext, base64 encoded, empty AAD
  - Key derivation matches: ExportSecret("marmot", "group-event", 32) on both sides
  - The OperationError is from Web Crypto API ChaCha20-Poly1305 decrypt failure
  - Root cause: MLS epoch state divergence between ts-mls and marmot-cs after Welcome processing
  - Both clients derive different exporter secrets for the same group+epoch
- **Action:** Requires comparing actual exporter secret bytes on both sides at the same epoch

## Issue 3: OpenChat uses real pubkey for kind 445, web uses ephemeral
- **MIP-03 spec:** Kind 445 events SHOULD use ephemeral keys for sender privacy
- **marmot-ts:** Uses `generateSecretKey()` ephemeral key per event
- **OpenChat:** Uses `LocalNostrEventSigner` with the user's real key
- **Impact:** Privacy leak — relay operators can link kind 445 messages to user identity
- **Fix needed in OpenChat:** Sign kind 445 events with ephemeral keys

## Verified Compatibility
- Key package format (kind 443): Compatible — tags, encoding, structure all match
- Gift wrap format (kind 1059): Compatible — Welcome successfully received and processed
- MIP-03 wire format: Compatible — nonce || ciphertext, base64, empty AAD

## Status
- [ ] Report Issue 1 to marmot-ts
- [ ] Investigate Issue 2 (epoch divergence)
- [ ] Fix Issue 3 (ephemeral keys for kind 445)
