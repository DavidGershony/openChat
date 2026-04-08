# M4 - Generate MIP-04 Nonce Internally Instead of Accepting from Caller

**Severity**: MEDIUM
**Status**: TODO

## Context

`Mip04MediaCrypto.EncryptMediaFile()` takes `byte[] nonce` as a parameter. If any caller reuses a nonce with the same key, ChaCha20-Poly1305 security breaks completely (keystream reuse). The nonce should be generated inside the method to prevent misuse.

## Files
- `src/OpenChat.Core/Crypto/Mip04MediaCrypto.cs` (lines 81-89)

## Tasks
- [ ] Generate a random 12-byte nonce inside `EncryptMediaFile` using `RandomNumberGenerator.Fill()`
- [ ] Return both ciphertext and nonce (change return type or prepend nonce to output)
- [ ] Update all callers to use the returned nonce
- [ ] Update `DecryptMediaFile` if it depends on externally-provided nonce
- [ ] Add test: two encryptions of the same plaintext produce different ciphertexts (proves unique nonces)
