# Real-time messages not appearing until app restart

## Status: Open

## Symptom

After the dotnet-mls v0.1.0-alpha.16 / MarmotCs.Core v0.1.0-alpha.14 update (commits now use PrivateMessage wire format instead of PublicMessage), desktop stops showing new incoming messages in real-time. Messages only appear after closing and reopening the app.

Mobile sends messages successfully (they appear on desktop after restart), and desktop can send messages that mobile receives. The MLS layer is working — encryption/decryption succeeds. The issue is in the app's real-time UI update pipeline.

## Context: What changed in MLS

- `MlsGroup.Commit()` now returns `PrivateMessage` instead of `PublicMessage`
- `MlsGroup.ProcessCommit()` now accepts `PrivateMessage` instead of `PublicMessage`
- `Mdk.ProcessMessageAsync` was updated to route `PrivateMessage` by `ContentType`:
  - `ContentType.Application` -> `DecryptApplicationMessage` (chat messages)
  - `ContentType.Commit` -> `ProcessCommit` (epoch advances)
- The old code routed ALL `PrivateMessage` to `DecryptApplicationMessage` and ALL `PublicMessage` to `ProcessCommit`

## Likely root cause

In `Mdk.cs` `ProcessMessageAsync`, incoming kind 445 events are now parsed as `PrivateMessage` (since commits changed to PrivateMessage). The routing by `ContentType` was added, but there may be an issue with how the result flows back to the UI:

1. **Check `HandleGroupMessage` in `MessageService.cs`** — does it properly handle the result from `ProcessMessageAsync` when the message is an application `PrivateMessage`? Does it notify the UI observable (`_chatMessages` / `OnNext`)?

2. **Check the "already-processed" dedup** — the desktop log shows ALL incoming kind 445 events being skipped as "already-processed event". This could mean:
   - Events from the old session are being replayed from relays (normal, expected to be skipped)
   - BUT new events from mobile might also be incorrectly marked as processed
   - Check if `SaveProcessedMessageAsync` is being called with the wrong event ID, or if the `since` filter on the group subscription is stale

3. **Check group subscription timing** — the subscription uses `"since":1775416062` which is a Unix timestamp. If this timestamp is in the future or too recent, new events might be filtered out by relays.

## Log evidence

**Desktop log:** `AppData/Local/OpenChat-Dev/profiles/e9b03d7d20c787ce/logs/openchat-20260405.log`

- Line 3083-3097: Group subscription is set up correctly after restart
- Lines 3283-3305: All incoming kind 445 events hit "skipping already-processed event"
- No new events appear after the initial replay from relays
- No decrypt errors or MLS failures — the issue is upstream of MLS

**Mobile log:** `ai-tasks/openchat-20260405-now.log`

- Mobile sends messages successfully (line 2351-2363)
- No MLS errors on mobile side

## How to reproduce

1. Start desktop and mobile, both logged in to the same Nostr account
2. Create a chat from desktop to a contact
3. Accept on mobile
4. Send a message from mobile
5. Desktop does NOT show the message in real-time
6. Restart desktop -> message appears

## Files to investigate

- `src/OpenChat.Core/Services/MessageService.cs` — `HandleGroupMessage`, `OnNostrEventReceived`, message dedup logic
- `src/OpenChat.Core/Services/NostrService.cs` — group subscription setup, `since` filter, event routing
- `src/OpenChat.Presentation/ViewModels/ChatViewModel.cs` — message list observable binding

## Not related to

- MLS encryption/decryption (working correctly)
- ProcessCommit (working correctly)
- Welcome processing (working correctly)
- The duplicate chat fix (separate issue, already fixed in commit 47e1411)
