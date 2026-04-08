# Task: Extract NostrRelayConnection wrapper around ClientWebSocket

## Status: Not started

## Decision log

**2026-04-08**: Originally planned to adopt `Websocket.Client` by Marfusios, but the library
has unresolved critical issues (memory leak #124 during prolonged reconnect, deadlock on Stop #139)
and has not been updated since Sep 2025. Both issues are especially bad for a multi-relay Nostr app.

**New plan**: Write a thin `NostrRelayConnection` wrapper (~200-300 lines) around .NET 9's
built-in `ClientWebSocket`. No third-party dependency. .NET 9's `KeepAliveTimeout` gives us
dead-connection detection for free.

This task also unblocks security fix H2 (subscription filter validation) — the wrapper's
reconnect handler needs centralized subscription tracking, which is exactly what H2 needs.

## Context

The current `NostrService` (~2,800 lines) hand-rolls all WebSocket handling: connection setup,
frame reassembly, listener tasks, and reconnection. This works but has real problems:

- **No auto-reconnect** — a dropped relay connection stays dead until manual `ReconnectRelayAsync()`
- **Fire-and-forget listener tasks** — `_ = ListenToRelayAsync(...)` with no crash recovery
- **TOCTOU race on sends** — `if (ws.State == Open) { SendAsync() }` can throw if state changes
- **No send timeout** — `CancellationToken.None` on all sends, can hang indefinitely
- **11 ad-hoc subscription sites** — no central tracking of what's subscribed, making reconnection
  fragile and H2 filter validation impossible

## Goal

Extract a `NostrRelayConnection` class that encapsulates one relay connection with:
- Thread-safe sends (SemaphoreSlim)
- Auto-reconnect with exponential backoff
- .NET 9 KeepAliveInterval + KeepAliveTimeout for dead-connection detection
- Receive loop pumping into `IObservable<string>` (fits existing Rx/ReactiveUI stack)
- Centralized subscription tracking: `RegisterSubscription(subId, filter)` / `UnregisterSubscription(subId)`
- Auto re-subscribe on reconnect from the tracked filter set
- H2 filter validation: incoming EVENT checked against registered subscription ID

## What NostrRelayConnection provides

| Feature | Current code | With wrapper |
|---|---|---|
| Auto-reconnect | None | Built-in, exponential backoff |
| Dead connection | Not detected | .NET 9 KeepAliveTimeout |
| Send model | Direct to socket (race) | SemaphoreSlim-guarded, with timeout |
| Frame reassembly | Manual loop + MemoryStream | Same loop, encapsulated |
| Message stream | Fire-and-forget task → Subject | IObservable<string> from receive loop |
| Subscription tracking | None (11 ad-hoc sites) | ConcurrentDictionary<subId, filter> |
| Reconnect re-subscribe | Reconstructs from scattered state | Replays tracked subscriptions |
| H2 filter validation | Not implemented | Check EVENT subId against tracked filters |

## Scope of changes

### New class: `NostrRelayConnection` (~200-300 lines)
- Constructor takes relay URL, creates ClientWebSocket with KeepAlive config
- `ConnectAsync()` — connect + start receive loop
- `SendAsync(string message)` — SemaphoreSlim-guarded send with timeout
- `SubscribeAsync(string subId, object filter)` — send REQ, track in dictionary
- `UnsubscribeAsync(string subId)` — send CLOSE, remove from dictionary
- `Messages: IObservable<string>` — message stream
- `ConnectionStatus: IObservable<bool>` — connected/disconnected
- Auto-reconnect loop with backoff, re-sends all tracked subscriptions on reconnect
- `ValidateEventSubscription(string subId, int eventKind)` — H2 check

### Refactor NostrService (~377 lines replaced)
- Replace `ConcurrentDictionary<string, ClientWebSocket> _relayConnections` with `<string, NostrRelayConnection>`
- Replace all 11 inline REQ sites with `connection.SubscribeAsync(subId, filter)`
- Remove `ListenToRelayAsync`, `ReceiveFullMessageAsync` (moved into wrapper)
- Remove manual reconnect logic (wrapper handles it)
- Add H2 validation in `ProcessRelayMessageAsync`

### Keep untouched (~1,100+ lines of protocol logic)
- `ProcessRelayMessageAsync()` — event routing, OK/AUTH/NOTICE handling
- `HandleAuthChallengeAsync()` — NIP-42
- `CreateGiftWrapAsync()` / `UnwrapGiftWrapAsync()` — NIP-59
- All publish methods, all NIP implementations
- Key management, signature verification, event parsing

## Implementation order

1. Create `NostrRelayConnection` class with connect, send, receive, auto-reconnect
2. Add subscription tracking (register/unregister/replay on reconnect)
3. Replace `ConnectToRelayAsync` to create `NostrRelayConnection` per relay
4. Replace all 11 REQ sites with `connection.SubscribeAsync()`
5. Wire `Messages` observable → `ProcessRelayMessageAsync` (existing method)
6. Wire `ConnectionStatus` → existing `_connectionStatus` subject
7. Add H2 filter validation on incoming EVENTs
8. Remove dead code: `ListenToRelayAsync`, `ReceiveFullMessageAsync`, manual reconnect
9. Test: connect, subscribe, receive events, publish, reconnect after drop, large messages

## Why not Websocket.Client

- Memory leak during prolonged reconnect (GitHub #124, still open)
- Deadlock on Stop() (GitHub #139, still open)
- Last commit Sep 2025, low-frequency maintenance
- Both issues are especially problematic for multi-relay Nostr apps that frequently
  connect/disconnect/reconnect
- .NET 9 ClientWebSocket + KeepAliveTimeout covers the main gap (dead-connection detection)
  that originally motivated the library dependency
