# Task: Replace raw ClientWebSocket with Websocket.Client library

## Status: Not started

## Context

The current `NostrService` (~2,600 lines) hand-rolls all WebSocket handling: connection setup,
frame reassembly, listener tasks, and reconnection. This works but has real problems:

- **No auto-reconnect** — a dropped relay connection stays dead until manual `ReconnectRelayAsync()`
- **Fire-and-forget listener tasks** — `_ = ListenToRelayAsync(...)` with no crash recovery
- **TOCTOU race on sends** — `if (ws.State == Open) { SendAsync() }` can throw if state changes between check and send
- **No send timeout** — `CancellationToken.None` on all sends, can hang indefinitely
- **Silent failures** — send errors logged and swallowed, caller never knows

The original plan was to integrate `Blockcore.Nostr.Client` (a Nostr protocol library), but analysis
showed that **none of the .NET Nostr libraries implement NIP-42, NIP-46, NIP-59, or NIP-65** — all of
which our NostrService already handles. Wrapping a Nostr library would mean reimplementing most of
our protocol logic on top of it.

The real value is in the **underlying WebSocket client**: `Websocket.Client` by Marfusios (MIT, 756
GitHub stars, used in production by multiple projects).

## Goal

Replace `System.Net.WebSockets.ClientWebSocket` with `Websocket.Client` in NostrService for
connection management only. Keep all NIP/Marmot protocol logic (~1,100+ lines) untouched.

## What Websocket.Client gives us

| Feature | Current code | With Websocket.Client |
|---|---|---|
| Auto-reconnect | None | Built-in, configurable delays |
| Disconnect detection | Listener exits silently | `DisconnectionHappened` observable with reason |
| Reconnect notification | None | `ReconnectionHappened` observable |
| Send model | Direct to socket (race condition) | Channel-based queue, thread-safe |
| Frame reassembly | Manual loop + MemoryStream | Built-in with RecyclableMemoryStream |
| Message stream | Fire-and-forget task → Subject.OnNext() | Native IObservable<ResponseMessage> |
| Close handling | Check in receive loop | Automatic detection + reconnect |

## What to watch out for

1. **Inactivity timeout** — defaults to 1 minute, triggers reconnect on quiet connections.
   Nostr relays can be quiet between messages. Must raise `ReconnectTimeout` or implement
   keep-alive pings to avoid unnecessary reconnections.

2. **Buffer size** — hardcoded 4KB chunks (our code uses 64KB). Reassembly still works
   correctly, just more iterations for large MLS Welcome events. Not a real problem.

3. **Re-subscribe on reconnect** — each reconnect creates a new `ClientWebSocket` instance.
   Must re-send all active subscriptions (Welcome + Group message filters) in the
   `ReconnectionHappened` handler. We already have `ResendSubscriptionsToRelayAsync()` for this.

4. **Library health** — 58 open GitHub issues including reconnection memory leaks (#124)
   and deadlocks on Stop() (#139). Works in production but not heavily maintained.
   Last commit: Sep 2025.

## Package

```xml
<PackageReference Include="Websocket.Client" Version="5.3.0" />
```

Dependencies it brings: `System.Reactive` (already used via ReactiveUI),
`Microsoft.IO.RecyclableMemoryStream`, `Microsoft.Extensions.Logging.Abstractions`.

## Scope of changes

### Replace (~377 lines of WebSocket plumbing)

| Current code | Replace with |
|---|---|
| `ConcurrentDictionary<string, ClientWebSocket> _relayConnections` | `ConcurrentDictionary<string, WebsocketClient> _relayConnections` |
| `ConnectToRelayAsync()` — manual socket create, connect, fire-and-forget listener | Create `WebsocketClient`, configure timeouts, subscribe to observables, `Start()` |
| `ListenToRelayAsync()` — manual receive loop | `client.MessageReceived.Subscribe(msg => ProcessRelayMessageAsync(...))` |
| `ReceiveFullMessageAsync()` — manual frame accumulation | Removed (library handles it) |
| `DisconnectSingleRelayAsync()` — manual close + cleanup | `client.Stop()` + `Dispose()` |
| `ResendSubscriptionsToRelayAsync()` — called manually | Wire into `client.ReconnectionHappened.Subscribe(...)` |
| Direct `ws.SendAsync()` with state check | `client.Send(message)` (queued, thread-safe) |

### Keep untouched (~1,100+ lines of protocol logic)

- `ProcessRelayMessageAsync()` — event routing, OK/AUTH/NOTICE handling
- `HandleAuthChallengeAsync()` — NIP-42
- `CreateGiftWrapAsync()` / `UnwrapGiftWrapAsync()` — NIP-59
- `Nip44EncryptAsync()` / `Nip44DecryptAsync()` — NIP-44
- All publish methods — event serialization, signing, relay broadcast
- All fetch methods — KeyPackages, metadata, relay lists
- All subscription filter construction
- Key management, signature verification, event parsing

### Rx integration

The current code uses hand-rolled Rx: fire-and-forget tasks that call `Subject<T>.OnNext()`.
With Websocket.Client, the message stream is a native `IObservable<ResponseMessage>` that
pipes directly into the existing Rx chain. This fits naturally with the ReactiveUI stack
already used throughout the app.

## Implementation order

1. Add `Websocket.Client` NuGet package
2. Replace connection setup (`ConnectToRelayAsync`) — create `WebsocketClient` per relay
3. Wire `MessageReceived` → `ProcessRelayMessageAsync` (existing method, no changes)
4. Wire `ReconnectionHappened` → `ResendSubscriptionsToRelayAsync` (existing method)
5. Wire `DisconnectionHappened` → connection status observable (existing `_connectionStatus`)
6. Replace send calls — `ws.SendAsync()` → `client.Send()`
7. Remove `ListenToRelayAsync`, `ReceiveFullMessageAsync` (dead code after migration)
8. Test: connect, subscribe, receive events, publish, reconnect after drop, large messages

## Not in scope

- No changes to INostrService interface
- No changes to NIP implementations (42, 44, 46, 59, 65)
- No changes to Marmot event handling (kinds 443, 444, 445)
- No Blockcore.Nostr.Client or NNostr.Client integration
- No changes to ExternalSignerService

## Estimated effort

~1-2 days. Most time spent on testing reconnection behavior and verifying large MLS messages
reassemble correctly through the 4KB chunk path.
