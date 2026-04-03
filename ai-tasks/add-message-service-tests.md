# Add MessageService unit tests

## Status: Not Started

## Problem

`MessageService` is the central routing/orchestration layer — it handles kind 445 (group messages), kind 444 (welcomes), kind 7 (reactions), kind 9 (messages), pending invites, chat lifecycle, and message persistence. It has zero dedicated tests despite being the most critical service in the app.

## What to test

### Message routing
- [ ] Kind 445 received → decrypted → inner kind 9 saved as Message
- [ ] Kind 445 received → decrypted → inner kind 7 routed as reaction update
- [ ] Kind 444 (welcome) → saved as PendingInvite → `NewInvites` observable fires
- [ ] Unknown inner kind → logged warning, not saved as message
- [ ] Duplicate event ID → ignored (dedup)

### Send flow
- [ ] `SendMessageAsync` for group chat → encrypts via MLS → publishes kind 445
- [ ] `SendMessageAsync` → message saved locally with Pending status → updated to Sent after publish
- [ ] `SendMessageAsync` with MLS failure → message status updated to Failed

### Chat lifecycle
- [ ] `GetChatsAsync` returns chats ordered by pinned first, then LastActivityAt
- [ ] New message updates chat.LastActivityAt and chat.LastMessage
- [ ] Accept invite → creates Chat + joins MLS group

### Error handling
- [ ] Decrypt failure → logged, not crash
- [ ] Missing group ID in event → logged warning
- [ ] Null/empty content → handled gracefully

## Approach

Use mocks for `INostrService`, `IMlsService`, `IStorageService`. Feed events through the `INostrService.Events` observable (use `Subject<NostrEventReceived>`). Verify behavior via storage mock calls and observable emissions.
