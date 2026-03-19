# Performance Quick Wins

## 1. Parallelize discovery relay queries
- **Location:** `NostrService.FetchRelayListAsync` (line 1957-1985)
- **Problem:** Loops through ~5 discovery relays sequentially, each with 10s timeout
- **Fix:** `Task.WhenAll` instead of sequential foreach
- **Impact:** 10-50s → 2-5s (worst case 10s instead of 50s)

## 2. Cache relay list, eliminate redundant FetchRelayListAsync call
- **Location:** `MainViewModel.InitializeAfterLoginAsync` calls it at line 422, then `FetchKeyPackagesAsync` calls it again at line 1282
- **Fix:** Cache result from first call, pass it to FetchKeyPackagesAsync or store on NostrService
- **Impact:** Eliminate redundant 2-10s network round-trip

## 3. Show chat list from DB immediately, relay data in background
- **Location:** `MainViewModel.InitializeAfterLoginAsync` lines 265-562
- **Problem:** User sees spinner until all relay operations complete
- **Fix:** Load chats from DB and show immediately, then connect relays and fetch data in background
- **Impact:** Perceived load time drops from 10-30s to <1s

## 4. Batch sender resolution (SQL JOIN or IN clause)
- **Location:** `MessageService.GetMessagesAsync` line 92
- **Problem:** Per-message `GetUserByPublicKeyAsync` — 50 DB queries for 50 messages
- **Fix:** Single query with `WHERE PublicKeyHex IN (...)` or JOIN in the messages query
- **Impact:** 250-500ms → ~10ms

## 5. Reuse existing relay connections for fetches
- **Location:** `FetchKeyPackagesAsync`, `FetchMetadataAsync`, `FetchRelayListFromRelayAsync` all create new WebSocket connections
- **Problem:** Each creates a new connection (DNS + TCP + WebSocket handshake) instead of using already-connected relays
- **Fix:** Check `_relayConnections` first, only create new connection if relay isn't already connected
- **Impact:** Save 500ms-5s per fetch operation

## 6. Parallelize per-group MLS state loading
- **Location:** `MainViewModel.InitializeAfterLoginAsync` lines 361-378
- **Problem:** Each group's MLS state loaded one-by-one from DB
- **Fix:** `Task.WhenAll` or single batch query
- **Impact:** 50-100ms savings for 10 groups

## 7. Batch last-message fetch for chat list
- **Location:** `MessageService.GetChatsAsync` lines 71-75
- **Problem:** Per-chat `GetMessagesForChatAsync(chat.Id, 1, 0)` — N queries for N chats
- **Fix:** Single SQL query with GROUP BY or window function to get last message per chat
- **Impact:** 100-200ms → ~10ms for 20 chats

## Status
- [x] #1 Parallelize discovery relays — `Task.WhenAll` in FetchRelayListAsync
- [x] #3 Show chats from DB immediately — split InitializeAfterLoginAsync into fast DB path + background InitializeNetworkAsync
- [x] #4 Batch sender resolution — deduplicate sender keys, query once per unique sender instead of per message
- [x] #7 Batch last-message fetch — new `GetLastMessagePerChatAsync()` with single SQL GROUP BY query
- [x] KeyPackage check + metadata fetch run in parallel in background
- [x] Welcome + group message subscriptions sent in parallel
- [ ] #2 Cache relay list (FetchRelayListAsync still called in FetchKeyPackagesAsync — but now in background, not blocking UI)
- [ ] #5 Reuse existing relay connections (deferred to Blockcore migration)
- [ ] #6 Parallelize MLS state loading (minor, already fast enough)
