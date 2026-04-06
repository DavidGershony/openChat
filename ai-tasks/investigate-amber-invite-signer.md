# Investigate: Does invite flow work with Amber (external signer)?

## Question
When inviting a user to a group via `ChatViewModel.SendInviteAsync`, the code passes
`_currentUserPrivateKeyHex` to `PublishWelcomeAsync`. For Amber users this is null.
`PublishWelcomeAsync` falls back to the external signer at line 1093 — but does this
actually work end-to-end?

## Things to Check
1. Does `CreateGiftWrapAsync` work with external signer (null private key)?
   - Gift wrap requires creating a Seal (kind 13) signed by the sender
   - The Seal content is NIP-44 encrypted — does NIP-44 work via external signer?
   - The Gift Wrap outer event uses an ephemeral key (not the signer) — that part should work
2. Does the external signer remain connected during the invite flow?
   - Android: signer can disconnect when app backgrounds
   - Desktop: signer WebSocket may timeout
3. Is `ManagedMlsService.AddMemberAsync` affected by null private key?
   - MLS uses its own signing keys (not the Nostr key), so probably fine
4. Test: log in with Amber on desktop, create a group, invite a user — check logs

## Key Code Paths
- `ChatViewModel.SendInviteAsync()` line 577: passes null privkey
- `NostrService.PublishWelcomeAsync()` line 1088-1102: signer fallback
- `NostrService.CreateGiftWrapAsync()`: NIP-44 encrypt + signing
- `ExternalSignerService`: nip44_encrypt, sign_event permissions

## Status
- [ ] Trace CreateGiftWrapAsync with external signer path
- [ ] Check if NIP-44 encrypt via signer is implemented
- [ ] Test on desktop with Amber
