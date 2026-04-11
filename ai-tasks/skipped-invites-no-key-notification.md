# Skipped Invites — No Key Available Notification

## Goal
When a kind-444 Welcome event arrives and we don't have the MLS key material to process it, silently dismiss it (so it never re-surfaces) and show a persistent, dismissable notice in the chat list UI: "N group invite(s) received — encryption keys not available on this device."

## Motivation
On a fresh install / new login with the same Nostr identity (e.g. via Amber), old Welcome events replay from the relay. The user has no key material for them so they can never be accepted. We should not flood the invite list with un-actionable items, but the user needs to know what happened.

## Steps

- [x] Add `CanProcessWelcomeAsync(byte[] welcomeData)` to `IMlsService`
- [x] Implement in `ManagedMlsService`: false if 0 stored keys; try `PreviewWelcomeAsync` per key otherwise
- [x] Implement in `MlsService` (Rust backend): return `true` (pass-through — can't check without attempting)
- [x] Add `GetSkippedInviteCountAsync`, `IncrementSkippedInviteCountAsync`, `ResetSkippedInviteCountAsync` to `IStorageService` / `StorageService` (thin wrappers over `AppSettings`)
- [x] Add `SkippedInvites` observable (`IObservable<Unit>`) to `IMessageService` / `MessageService`
- [x] Update `HandleWelcomeEventAsync`: if `CanProcessWelcomeAsync` returns false → `DismissWelcomeEventAsync` + `IncrementSkippedInviteCountAsync` + `_skippedInvites.OnNext`
- [x] Update `ChatListViewModel`: load `SkippedInviteCount` on init, subscribe to `SkippedInvites`, add `DismissSkippedInviteNoticeCommand` (calls `ResetSkippedInviteCountAsync`)
- [x] Add info bar to `fragment_chat_list.xml` (below pending invites section, above chat list)
- [x] Bind info bar in `ChatListFragment` to `SkippedInviteCount`
- [x] Write unit tests for the new skip-and-dismiss path

## Status: COMPLETE
All source projects build cleanly. 313/314 unit tests pass (1 pre-existing failure in RelayUrlValidationTests unrelated to this feature). Mip03InteropTests build error is a pre-existing issue being fixed by a separate agent.

## Key files
- `src/OpenChat.Core/Services/IMlsService.cs`
- `src/OpenChat.Core/Services/ManagedMlsService.cs`
- `src/OpenChat.Core/Services/MlsService.cs`
- `src/OpenChat.Core/Services/IStorageService.cs`
- `src/OpenChat.Core/Services/StorageService.cs`
- `src/OpenChat.Core/Services/IMessageService.cs`
- `src/OpenChat.Core/Services/MessageService.cs`
- `src/OpenChat.Presentation/ViewModels/ChatListViewModel.cs`
- `src/OpenChat.Android/Fragments/ChatListFragment.cs`
- `src/OpenChat.Android/Resources/layout/fragment_chat_list.xml`
