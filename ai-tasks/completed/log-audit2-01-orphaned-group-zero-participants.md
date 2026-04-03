# Task: Fix orphaned group chats with 0 participants

## Status: COMPLETED

## Epic: log-audit-2 (2026-04-02 remote device log analysis)

## Priority: P0

## Problem

A group chat (`467a01be`) exists in the DB with name "Chat with npub13tq2ykq..." but has **0 participants** and the current user is NOT in the participant list. The chat is marked as orphaned on load.

From log line 12280:
```
[WRN] LoadChats: chat 467a01be 'Chat with npub13tq2ykq...' (type="Group") has 0 participants
      but current user NOT found. Participants: []. Marking as orphaned.
```

This is likely related to issue #7 (MLS group state not persisted) — the group was created or joined but the participant list was never saved to the DB, or was cleared during a failed operation.

## Goal

1. Investigate why the participant list is empty — trace the group creation and Welcome acceptance flows to find where `ParticipantPublicKeys` should be populated
2. Ensure that when a group is created or a Welcome is accepted, the participant list is always saved
3. Ensure that when a Welcome is processed and the group is reconstructed from relay data, participants are populated from the MLS group membership
4. Add a repair path: if a group exists with 0 participants but has valid MLS state, reconstruct the participant list from the MLS group
