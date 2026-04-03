# Task: Fix MLS group state not persisting across app restarts

## Status: COMPLETED — MLS state persistence is working correctly. The "Restored 0" in early sessions was because no groups existed yet. The real issue was #1 (participant list wiped by SaveChatAsync).

## Epic: log-audit-2 (2026-04-02 remote device log analysis)

## Priority: P0

## Problem

On every app startup, `Restored 0 MLS group states from persistence` — yet the app later successfully encrypts messages for group `467a01be2f7509c2`. This means group state is reconstructed at runtime from Welcome messages rather than being reliably persisted.

From log lines 49 and 3132:
```
[INF] Restored 0 MLS group states from persistence
...
[DBG] EncryptMessage: group=467a01be2f7509c2, plaintext length=2, tags=0 (managed)
```

If the Welcome message is no longer available from relays (e.g. relay prunes old events, or relay is down), the group becomes permanently unusable — explaining the orphaned chat in issue #1.

## Goal

1. Investigate why MLS group states are not being saved to persistence — trace `PersistGroupStateAsync` / `SaveMlsGroupState` calls
2. Check if the persistence is failing silently (exception swallowed) or if it's never called after group creation/Welcome acceptance
3. Ensure group state is persisted immediately after:
   - Creating a new group
   - Processing a Welcome message
   - Processing a commit that changes the group state
4. Add a failing test that creates a group, restarts the service, and verifies the group state is restored
