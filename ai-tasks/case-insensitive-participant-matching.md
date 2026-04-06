# Fix: Case-Insensitive Participant Key Matching (Orphan Chat Bug)

## Problem
Every chat opened on mobile automatically becomes "orphan" because participant public keys
stored in uppercase hex (from MLS `Convert.ToHexString`) don't match the current user's
lowercase hex pubkey. The case-sensitive `Contains()` check in `ChatListViewModel.LoadChatsAsync`
fails, marking the user as "not a participant" in their own chats.

## Root Cause
- `ManagedMlsService.ProcessWelcomeAsync` returns `MemberPublicKeys` from `preview.MemberIdentities`
  which come from `Convert.ToHexString()` — returns **UPPERCASE** hex
- `StorageService.GetCurrentUserAsync()` returns `PublicKeyHex` in **lowercase**
- `ChatListViewModel` line 368: `chat.ParticipantPublicKeys.Contains(_currentUserPubKeyHex)` is case-sensitive

## Log Evidence (2026-04-06)
```
[INF] LoadChats: currentUserPubKey=8ac0a2580137da29
[WRN] LoadChats: chat 21b6cbf4 ... Participants: [8AC0A2580137DA29, E9B03D7D20C787CE]. Marking as orphaned.
```

## Fix
1. Normalize `MemberPublicKeys` to lowercase in `ManagedMlsService` at all return points
2. Normalize sender hex to lowercase in `DecryptMessageAsync`
3. Case-insensitive `Contains` in `ChatListViewModel` as defense-in-depth
4. Normalize in `StorageService.SaveChatAsync` at the storage boundary

## Steps
- [x] Create task file
- [x] Write failing test
- [x] Fix ManagedMlsService — normalize MemberPublicKeys to lowercase
- [x] Fix ManagedMlsService — normalize senderHex to lowercase in DecryptMessageAsync
- [x] Fix ChatListViewModel — case-insensitive Contains
- [x] Fix MlsService (Rust backend) — normalize MemberPublicKeys to lowercase
- [x] Fix StorageService — normalize on save
- [x] Run tests, confirm pass (196 UI passed, 0 failed; core failures all pre-existing relay issues)
- [ ] Commit
