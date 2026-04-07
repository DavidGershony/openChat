# Add MessageService unit tests

## Status: Complete

## Problem

`MessageService` is the central routing/orchestration layer — it handles kind 445 (group messages), kind 444 (welcomes), kind 7 (reactions), kind 9 (messages), pending invites, chat lifecycle, and message persistence. It has zero dedicated tests despite being the most critical service in the app.

## What to test

### Message routing
- [x] Kind 445 received → decrypted → inner kind 9 saved as Message
- [x] Kind 445 received → decrypted → inner kind 7 routed as reaction update
- [x] Kind 444 (welcome) → saved as PendingInvite → `NewInvites` observable fires
- [x] Kind 14 (bot DM) → saved as Message in bot chat
- [x] Duplicate event ID → ignored (dedup)
- [x] Commit message (IsCommit) → no user message saved
- [x] Audio imeta → MessageType.Audio
- [x] Image imeta → MessageType.Image

### Send flow
- [x] `SendMessageAsync` for group chat → encrypts via MLS → publishes kind 445
- [x] `SendMessageAsync` → message saved locally with Pending status → updated to Sent after publish
- [x] `SendMessageAsync` with MLS failure → message status updated to Failed
- [x] `SendMessageAsync` for bot chat → uses NIP-17 gift wrap
- [x] `SendMessageAsync` when not logged in → throws

### Chat lifecycle
- [x] `GetChatsAsync` returns chats with batch-loaded last messages
- [x] Accept invite → creates Chat + joins MLS group
- [x] Accept invite with duplicate group → returns existing chat
- [x] Decline invite → dismisses and deletes
- [x] GetOrCreateDirectMessage → returns existing or creates new
- [x] Archive chat → sets archived flag
- [x] Set muted → updates muted status
- [x] Leave group → cleans up local state

### Error handling
- [x] Decrypt failure → emits DecryptionError, no crash
- [x] Missing group ID in event → no save, no crash
- [x] No matching chat for group → no save, no crash
- [x] Dismissed welcome event → skipped
- [x] Self-echo bot message → skipped
- [x] MLS init failure → does not throw
- [x] No current user → skips MLS and subscription

## Approach

Use mocks for `INostrService`, `IMlsService`, `IStorageService`. Feed events through the `INostrService.Events` observable (use `Subject<NostrEventReceived>`). Verify behavior via storage mock calls and observable emissions.
