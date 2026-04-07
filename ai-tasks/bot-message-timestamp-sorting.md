# Bot message timestamp sorting issue

## Status: Investigating

## Problem

Messages in bot chats appear sorted incorrectly. User reports the display order seems based on the gift-wrap timestamp rather than the inner rumor's `created_at`.

## Analysis

The code path looks correct:
- `UnwrapGiftWrapAsync` (NostrService.cs:1325-1347) extracts `rumor.created_at` and sets `CreatedAt` on the `NostrEventReceived`
- `HandleBotMessageEventAsync` (MessageService.cs:935) uses `nostrEvent.CreatedAt` for the message `Timestamp`
- `GetMessagesForChatAsync` (StorageService.cs:617) orders by `Timestamp DESC`

Possible causes:
1. The bot/sender randomizes the rumor's `created_at` (NIP-59 allows this for privacy)
2. The rumor's `created_at` is in a different timezone or epoch format
3. The sent message uses `DateTime.UtcNow` while the received message uses the rumor timestamp, causing slight ordering mismatches

## Next steps

- [ ] Add debug logging to `HandleBotMessageEventAsync` showing both `nostrEvent.CreatedAt` and `DateTime.UtcNow` to identify the delta
- [ ] Check the MCP nostr plugin's rumor creation to see if it randomizes `created_at`
- [ ] Reproduce with the user and inspect actual timestamp values in the DB
