# Task: Align Android new-chat flow with desktop — validate npub and auto-lookup KeyPackage

## Status: COMPLETED

## Epic: log-audit-2 (2026-04-02 remote device log analysis)

## Priority: P1

## Problem

On Android, the user can tap "Create" before the KeyPackage lookup completes, resulting in "no KeyPackage selected" errors. The desktop flow handles this better.

From log line 5343:
```
[WRN] CreateNewChat: no KeyPackage selected
```

## Goal

Align Android new-chat flow with desktop behavior:

1. **Validate npub on input** — check format is valid npub/hex, show inline error if invalid
2. **Reject self-npub** — show error if user enters their own npub
3. **Auto-lookup KeyPackage** — as soon as a valid npub is entered, immediately start fetching the KeyPackage in the background (no manual "Search" button needed)
4. **Disable Create button** — keep it disabled until:
   - A valid npub is entered
   - It's not the user's own npub
   - KeyPackage lookup has completed successfully
5. Show a loading indicator while the KeyPackage is being fetched
6. Show an error if no KeyPackage is found for the recipient
