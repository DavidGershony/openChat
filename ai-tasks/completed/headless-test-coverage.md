# Headless Integration Test Coverage

## Goal
Add headless integration tests for all untested desktop features.

## Messaging (8)
- [x] Send voice message (SendVoiceMessageAsync)
- [x] Send media/file (SendMediaMessageAsync, AttachFileCommand)
- [x] Send reply (SendReplyAsync)
- [x] Add/remove reactions (AddReactionAsync, RemoveReactionAsync)
- [x] Delete message (DeleteMessageAsync)
- [x] Mark as read (MarkAsReadAsync)
- [x] Archive chat (ArchiveChatAsync)
- [x] Mute/unmute chat (SetMutedAsync)

## DMs & Bot Chats (4)
- [x] Create direct message (GetOrCreateDirectMessageAsync)
- [x] Create bot/device chat (GetOrCreateBotChatAsync)
- [x] Join group by ID (JoinGroupAsync)
- [x] Lookup key packages (LookupKeyPackageCommand)

## Group Member Management (3)
- [x] Add member (AddMemberAsync)
- [x] Remove member (RemoveMemberAsync)
- [x] Leave group (LeaveGroupAsync)

## Chat UI Interactions (5)
- [x] Chat/group info panel
- [x] Invite dialog + copy group link
- [x] Recording UI state transitions
- [x] File attach flow (managed backend only — Rust lacks MIP-04 exporter secret)
- [ ] Audio playback

## Profile & Account (3)
- [x] Show my profile modal
- [x] Copy npub/nsec
- [x] Fetch & cache user profile

## Relay Management (2)
- [x] Cycle relay usage mode (Read/Write/Both)
- [x] Reconnect individual relay

## Debug (4)
- [x] Log viewer open
- [x] Log viewer close
- [x] Log viewer refresh
- [x] Open log folder

## Advanced (1)
- [x] Load older messages from relay (pagination)
