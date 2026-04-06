# Fix: NIP-17 Bot Messages Not Appearing in Real-Time on Mobile

## Problem
Bot messages (NIP-17 DMs) only appear after closing and reopening the app on mobile.
Messages are never received in real-time during a running session.

## Root Cause
When the app reconnects to relays (common on Android due to lifecycle management),
`ResendSubscriptionsToRelayAsync` re-subscribes to kind 1059 events with a `since`
filter set to the disconnect time. However, NIP-59 gift wraps use **randomized
`created_at` timestamps** for privacy. A bot's response gift wrap with a random
past timestamp gets filtered out by the relay because `created_at < since`.

The initial subscription (at app start) has no `since` filter, which is why ALL
historical events arrive on startup — explaining why messages appear after restart.

## Log Evidence
- Across ALL mobile logs: zero "HandleBotMessage: saved" entries outside of startup batch
- Apr 2 log: user sent bot DM at 00:02:47, app killed by Android at 00:04:10, no response received
- Desktop logs: bot messages arrive and are saved in real-time (no `since` issue on desktop
  because connections are more stable)

## Fix
Remove the `since` filter from kind 1059 reconnect subscriptions. Deduplication is
already handled at two levels:
1. In-memory: `_recentlyProcessedEventIds` ConcurrentDictionary
2. DB-level: `MessageExistsByNostrEventIdAsync`

## Steps
- [x] Investigate logs and code
- [x] Identify root cause (since filter + NIP-59 randomized timestamps)
- [x] Remove since filter from kind 1059 reconnect subscription
- [ ] Run tests
- [ ] Commit
