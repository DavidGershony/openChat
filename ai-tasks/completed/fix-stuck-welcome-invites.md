# Fix stuck Welcome invites after KeyPackage rotation

## Status: Completed

## Root Cause (from log analysis)

**A KeyPackage is single-use in MLS (RFC 9420).** When you accept an invite, `ProcessWelcomeAsync` consumes the matching KeyPackage and removes it from the stored list. If the sender created multiple groups using the SAME KeyPackage (fetched once from relays), only the first accept succeeds — subsequent invites targeting the same KP can never be processed.

**Timeline from the log:**
1. 21:43:08 — Receiver publishes new KP (`a01f0830`), stored KPs = 2 (old `5647c798` + new)
2. 21:43:55 — Sender fetches new KP `a01f0830`, creates group `3c318400`, sends Welcome
3. 21:44:10 — Receiver accepts invite for group `3c318400`. KP 2/2 matches. **KP consumed.** State saved as 474 bytes (1 KP remaining: old `5647c798`)
4. 21:45:46 — Second Welcome (rumor `664f7885`, also targeting KP `a01f0830`) arrives → ProcessWelcome fails: "Welcome does not contain secrets for our key package" — the KP was already consumed
5. Invite stays stuck in pending forever, user keeps retrying impossibly

## Fixes

### Fix 1: Auto-dismiss on KeyPackage mismatch (DONE)
In `AcceptInviteAsync`, catch "KeyPackage" mismatch and auto-dismiss the invite.
- File: `src/OpenChat.Core/Services/MessageService.cs`

### Fix 2: Mark consumed KeyPackage as used in DB (DONE)
After `AcceptInviteAsync` succeeds, the consumed KP (identified via `PendingInvite.KeyPackageEventId`) is
marked as used via `MarkKeyPackageUsedAsync`. This prevents reuse and allows audit tracking.
- File: `src/OpenChat.Core/Services/MessageService.cs`

### Fix 3: Auto-publish new KP when last one is consumed (DONE)
After marking a KP as used, `AutoPublishKeyPackageIfNeededAsync` checks remaining unused KPs.
If zero remain, it auto-generates and publishes a new one so the user stays invitable.
- File: `src/OpenChat.Core/Services/MessageService.cs`
