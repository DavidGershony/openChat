# SEC-HIGH: Thread Safety in NostrService

## Problem
`_subscriptions` (Dictionary), `_connectedRelays` (List), `_subscribedGroupIds` (HashSet) are not thread-safe but accessed from multiple async contexts (main thread, WebSocket listeners, event callbacks).

## Location
- `NostrService.cs:26-30` — field declarations

## Fix
- [ ] Replace `Dictionary<string, IDisposable>` with `ConcurrentDictionary`
- [ ] Replace `List<string> _connectedRelays` with `ConcurrentBag<string>` or use locking
- [ ] Replace `HashSet<string> _subscribedGroupIds` with `ConcurrentDictionary<string, byte>` (no ConcurrentHashSet in .NET)
- [ ] Audit all access points for remaining race conditions

## Tests Required
- [ ] Concurrent subscription calls don't throw
- [ ] Concurrent relay connect/disconnect don't corrupt state

## Status
- [ ] Not started
