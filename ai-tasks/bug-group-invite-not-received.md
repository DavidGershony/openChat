# Bug: Group invite not received by third user (cross-relay)

## Problem
When adding a third user to a group from desktop, the app says "user already added"
on retry but the other desktop never receives the invite. The issue persisted even
after connecting both desktops to the same relays.

## Root Cause (Two Issues)

### Issue 1: Participant added before Welcome is published
In `ChatViewModel.SendInviteAsync()`, the user is added to `ParticipantPublicKeys`
at line 590 **unconditionally** — even when the MLS operation fails and no Welcome
is ever published. On retry, the "already a member" check at line 535 blocks
re-invitation.

### Issue 2: DPAPI collision between multiple desktop profiles
The actual MLS `AddMemberAsync` call at `ManagedMlsService.cs:285` threw
`GroupNotFoundException` because the MLS group state wasn't properly restored.
The desktop logs show:
```
Failed to restore MLS service state, will generate new keys
System.Security.Cryptography.CryptographicException: The parameter is incorrect.
```
**Root cause:** DPAPI encrypts per-Windows-user, not per-app-profile. When two
desktop chat profiles run under the same Windows user, they share the same DPAPI
context. If profile A writes an MLS state blob encrypted with DPAPI, then profile B
writes to the same key, profile A's blob is overwritten. On next load, the decryption
succeeds for one profile but fails for the other — or the data is simply corrupted.

This is a fundamental issue: `DesktopSecureStorage` uses DPAPI with `DataProtectionScope.CurrentUser`,
which means ALL profiles on the same Windows account share the same encryption keys.
MLS state blobs from different profiles can collide in the `MlsStates` DB table if
they use similar group IDs or the storage keys overlap.

The group existed in the chat DB but was lost from the MLS engine, so the Welcome
could never be generated. Not related to recent fixes (hex case, NIP-59 timestamps).

## Log Evidence (April 6, 16:40:20-16:40:39)

### First invite attempt — MLS fails silently:
```
16:40:20 [INF] SendInvite: fetching KeyPackage for acf1a2f774e429ce
16:40:21 [INF] Found 1 KeyPackages (304 bytes, from wss://nos.lol)
16:40:21 [INF] MLS AddMembersAsync called for group 606764b50391f4df
16:40:21 [WRN] MLS operation failed - falling back to local invite
         GroupNotFoundException: Group 606764B50391F4DF79A79AF0BE1BB84C not found
16:40:21 [INF] Successfully invited acf1a2f774e429ce to group 606764b50391f4df
                ← MISLEADING: Welcome was never published!
```

### Second invite attempt — blocked by false positive:
```
16:40:39 [WRN] SendInvite: user acf1a2f774e429ce already a member
         ← Participant was added locally despite MLS failure
```

### Receiver side — subscription healthy, nothing to receive:
```
[INF] Subscribing to Welcome messages (kind 1059 gift wrap) for e9b03d7d20c787ce...
[INF] Sending Welcome subscription REQ: ["REQ","welcome_b89d8b46",{"kinds":[1059],...}]
         ← Subscription active, but no Welcome event was ever published
```

## Code Flow

### SendInviteAsync() in ChatViewModel.cs lines 496-613:
```
1. Check "already a member" (line 535) → blocks retry after first failure
2. Fetch KeyPackage (line 545) → succeeds
3. MLS AddMemberAsync (line 567) → THROWS GroupNotFoundException
4. Catch block logs warning (line 582) → continues execution
5. Add to ParticipantPublicKeys (line 590) → SHOULD NOT HAPPEN on failure
6. SaveChatAsync (line 594) → persists the false participant
7. Log "Successfully invited" (line 610) → MISLEADING
```

## Key Files
- `src/OpenChat.Presentation/ViewModels/ChatViewModel.cs` — SendInviteAsync() lines 496-613
  - Line 535-543: "already a member" guard (blocks re-invite)
  - Line 567: MLS AddMemberAsync call (throws)
  - Line 582-585: catch block (swallows error, continues)
  - Line 590: Unconditional participant add (THE BUG)
- `src/OpenChat.Core/Services/ManagedMlsService.cs` — AddMembersAsync() line 285

## Fix
1. Move `ParticipantPublicKeys.Add()` and `SaveChatAsync()` INSIDE the try block,
   after `PublishWelcomeAsync` succeeds — only add the participant when the Welcome
   was actually published to relays
2. Don't log "Successfully invited" when MLS failed — surface the error to the user
3. Consider a "re-invite" option that bypasses the "already a member" check and
   re-publishes the Welcome for participants who haven't accepted yet

## Steps
- [x] Investigate logs and code
- [x] Write failing tests (SendInvite_MlsFails_DoesNotAddParticipant, SendInvite_NoKeyPackage_ShowsError)
- [x] Fix: participant only added after PublishWelcomeAsync succeeds
- [x] Fix: missing KeyPackage now shows error instead of silent local add
- [x] Fix: MLS/publish errors now propagate to the outer catch (shows error to user)
- [x] Run tests (200 passed, 0 failed)
- [ ] Commit
