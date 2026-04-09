# Group Admin Support & Member List

**Priority**: HIGH
**Status**: PLANNED

## Problem

OpenChat ignores the admin system built into the Marmot protocol (MIP-01). The 0xF2EE
`NostrGroupData` extension carries an `AdminPubkeys` field — a vector of 32-byte Nostr
public keys designating group administrators. Other Marmot clients (Rust reference,
marmot-ts) use this field to gate admin-only operations and display admin badges.

Additionally, the group info panel in the chat view shows no member list at all — only a
participant count. Users cannot see who is in their group.

## What Marmot Defines (MIP-01 0xF2EE extension)

Wire format includes `admin_pubkeys` as `vector<V> of [u8; 32]`:
- Set at group creation: creator's pubkey added automatically by MDK (`Mdk.cs:88-113`)
- Transmitted in Welcome messages via MLS GroupContext extensions
- Can be updated via self-update commits (group metadata changes)
- Enforcement is application-layer — MLS itself doesn't restrict proposals

Source: `marmut-mdk/src/MarmotCs.Protocol/Mip01/NostrGroupDataExtension.cs`
```csharp
public byte[] AdminPubkeys { get; set; } = Array.Empty<byte>();
```

## What We're Missing

### 1. Not reading AdminPubkeys
- `ManagedMlsService` extracts group name and relays from 0xF2EE but never reads `AdminPubkeys`
- `MessageService.HandleWelcomeEventAsync` doesn't extract or store admin info

### 2. No admin storage
- `ChatParticipants` table has no role column (just ChatId + PublicKeyHex)
- `Chats` table has no admin/creator field
- No way to persist who the admins are

### 3. No permission enforcement
- `MessageService.AddMemberAsync` — any member can invite anyone
- `MessageService.RemoveMemberAsync` — any member can remove anyone
- No checks against AdminPubkeys before performing these operations

### 4. No admin UI
- No admin badge in any member display
- No promote/demote member functionality
- No "admin only" gating on invite/remove buttons

### 5. No member list in group info
- Desktop: Chat info panel shows contact metadata for DMs but has NO member list for groups
- Android: `bottom_sheet_group_info.xml` shows group name, participant count, and invite
  button — but no actual list of members
- Users cannot see who is in their group beyond the count

### 6. No group metadata editing
- Group name, description, avatar set at creation and never updated
- No UI to edit group settings after creation
- Protocol supports this via self-update commits with updated 0xF2EE extension

## Defaults We Ship vs What Other Marmot Clients Allow

| Setting | OpenChat Default | Other Marmot Clients |
|---------|-----------------|---------------------|
| Who can add members | Anyone | Admins only |
| Who can remove members | Anyone | Admins only |
| Who can edit group name/avatar | Nobody (immutable) | Admins |
| Admin list visible | No | Yes (badges in member list) |
| Member list visible | No (count only) | Yes (full list with roles) |
| Promote/demote members | Not possible | Admin can promote/demote |

## Implementation Plan

### Phase 1: Member List (no admin dependency)
1. Add `GroupMembers` observable collection to `ChatViewModel` — populated from
   `Chat.ParticipantPublicKeys` when loading a group chat
2. Fetch NIP-05/kind-0 metadata for each member (display name, avatar)
3. Desktop: Add scrollable member list section to the chat info panel (below group
   name, above invite button) — show avatar, display name, npub
4. Android: Add RecyclerView member list to `bottom_sheet_group_info.xml`

### Phase 2: Extract & Store Admin Data
5. Update `ManagedMlsService` to extract `AdminPubkeys` from the 0xF2EE extension
   when processing Welcomes and commits
6. DB migration: add `Role TEXT DEFAULT 'member'` column to `ChatParticipants`
7. DB migration: add `CreatorPublicKey TEXT` column to `Chats`
8. On Welcome processing, mark members listed in AdminPubkeys as role='admin'
9. On group creation, store creator pubkey

### Phase 3: Display Admin Status
10. Show admin badge (star/shield icon) next to admin names in member list
11. Show "Admin" label under admin names
12. Sort member list: admins first, then alphabetical

### Phase 4: Enforce Admin Permissions
13. Gate "Invite Member" button to admins only (or creator if no admin list)
14. Add "Remove Member" option in member list — admin only
15. Add "Leave Group" option accessible to all members
16. Show appropriate error/toast when non-admin tries restricted action

### Phase 5: Group Metadata Editing (admin only)
17. Add "Edit Group" UI for admins — name, description, avatar
18. On save, create self-update commit with updated 0xF2EE extension
19. Publish commit to relays, other members receive updated metadata
20. Add "Promote to Admin" / "Demote" context menu on member list items

## Files to Modify

**Core:**
- `src/OpenChat.Core/Services/ManagedMlsService.cs` — extract AdminPubkeys
- `src/OpenChat.Core/Services/MessageService.cs` — store admin roles, permission checks
- `src/OpenChat.Core/Services/StorageService.cs` — DB migration for role column
- `src/OpenChat.Core/Models/Chat.cs` — add CreatorPublicKey field

**Desktop UI:**
- `src/OpenChat.UI/Views/ChatView.axaml` — member list in info panel
- `src/OpenChat.Presentation/ViewModels/ChatViewModel.cs` — GroupMembers collection,
  admin display logic, permission-gated commands

**Android UI:**
- `src/OpenChat.Android/Resources/layout/bottom_sheet_group_info.xml` — member RecyclerView
- `src/OpenChat.Android/Fragments/ChatFragment.cs` — bind member list

## Out of Scope
- Invite links / QR codes for groups
- Public group directory
- Disappearing messages / message retention
- Read receipts
