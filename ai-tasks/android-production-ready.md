# Task: Android & Desktop Create Chat/Group Progress UI

## Status: Not Started

## Problem

When creating a chat/group, clicking "Create" disables the button but gives no feedback about what's happening. If a step takes a long time (e.g. signer timeout), the user has no idea what's stuck. The current 180s signer timeout makes this worse.

## Goal

Replace the disabled button state with a small progress dialog/overlay showing real-time status of each step in the group creation flow.

## Steps to Show Status For

### New Chat (CreateNewChatAsync)
1. "Creating MLS group..."
2. "Adding member to group..."
3. "Publishing Welcome message..." (NIP-59 gift wrap — involves signing, can be slow with external signer)
4. "Saving chat..."
5. "Subscribing to messages..."

### New Group (CreateNewGroupAsync)
1. "Creating MLS group..."
2. "Looking up KeyPackages..." (for each member)
3. "Adding members to group..." (per member)
4. "Publishing Welcome messages..." (per member, NIP-59 gift wrap)
5. "Saving group..."
6. "Subscribing to messages..."

## Implementation Approach

### ViewModel
- Add `[Reactive] public string? CreateProgress { get; set; }` to `ChatListViewModel`
- Set it at each step in `CreateNewChatAsync` / `CreateNewGroupAsync`
- Clear on completion or error

### Desktop (Avalonia)
- When `CreateProgress` is non-null, show a small centered overlay with:
  - Spinner/progress indicator
  - The status label text
  - Smaller than the create dialog, overlays on top of it

### Android
- In `NewChatFragment` / `NewGroupFragment`, bind to `CreateProgress`
- Show the sending overlay (already exists) with dynamic text instead of static "Sending invite..."

## Additional Improvements
- Consider reducing signer timeout from 180s to something more reasonable (30-60s)
- Show error inline in the progress overlay if a step fails
- Allow cancellation
