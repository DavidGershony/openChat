# M1 - Migrate NIP-46 Signer Communication from NIP-04 to NIP-44

**Severity**: MEDIUM
**Status**: TODO

## Context

NIP-46 (external signer / bunker) communication in `ExternalSignerService` uses NIP-04 (AES-256-CBC without authentication). NIP-04 is deprecated and vulnerable to:
- Padding oracle attacks (PKCS7 padding + CBC)
- Ciphertext malleability (no AEAD)

NIP-44 (ChaCha20-Poly1305 with AEAD) is already available in the codebase via MarmotCs.

## Files
- `src/OpenChat.Core/Services/ExternalSignerService.cs` (lines 957-975 NIP-04 encrypt, line 491 usage)

## Tasks
- [ ] Replace NIP-04 encrypt/decrypt calls with NIP-44 equivalents for NIP-46 communication
- [ ] Maintain NIP-04 decryption as fallback for backward compatibility with older signers
- [ ] Test with Amber and other NIP-46 signers to verify NIP-44 support
- [ ] Add integration test for NIP-46 round-trip with NIP-44 encryption
