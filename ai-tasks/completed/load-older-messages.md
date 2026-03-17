# Load Older Messages

## Goal
Add a "Load older messages" button in the chat view to fetch historical group messages from relays.

## Background
Currently, OpenChat only receives messages via live relay subscriptions (using `since` filter). Messages sent before the app was online or before the group was joined are not fetched. Users expect to see conversation history when opening a chat.

## Design
### Pagination strategy: 2-day sliding window with limit
- Each click moves the window back 2 days from the previous fetch boundary
- Use `limit: 50` to cap results per request
- If 0 results returned, silently retry up to 3 times (moving back 2 days each retry)
- Stop when `since` < group creation timestamp (`chat.CreatedAt`)

### Relay filter
```json
{"kinds":[445], "#h":["<nostrGroupId>"], "until": <lastWindow>, "since": <lastWindow - 2days>, "limit": 50}
```

### Flow control
1. User clicks "Load older messages"
2. Show loading spinner, disable button
3. Query relay with filter
4. If 0 results → retry with `since` moved back 2 more days (up to 3 attempts)
5. If still 0 or `since` < `chat.CreatedAt` → hide button ("No more messages")
6. Decrypt each event, deduplicate by `NostrEventId`
7. Insert messages into the list in correct chronological order (oldest at top)
8. Re-enable button

### Message ordering
- Messages must display in chronological order (oldest at top, newest at bottom)
- When inserting older messages, prepend to the top of the existing list
- Sort by `Timestamp` (from the Nostr event `created_at`)
- Preserve scroll position so the view doesn't jump when older messages are inserted

## Requirements
- [x] Add `FetchGroupHistoryAsync` method to `INostrService`/`NostrService` — fetches kind 445 events with `until`/`since`/`limit` filters
- [x] Add `LoadOlderMessagesAsync` to `MessageService` — calls fetch, decrypts, deduplicates, saves
- [x] Add `LoadOlderMessagesCommand` to `ChatViewModel` — triggers fetch, manages loading state
- [x] Add "Load older messages" button at top of chat message list in XAML
- [x] Track the current fetch window boundary per chat (in-memory, reset on chat switch)
- [x] Hide button when `since` reaches `chat.CreatedAt` or relay returns empty after retries
- [x] Ensure messages display in correct chronological order after insertion
- [x] Handle MLS decrypt failures gracefully (skip undecryptable messages, log warning)

## Status
- [x] Completed
