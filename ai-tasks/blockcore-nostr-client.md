# Task: Integrate Blockcore.Nostr.Client as switchable NostrService backend

## Context

The current `NostrService` (~1500 lines) hand-rolls all WebSocket handling, Nostr event parsing,
subscription management, and reconnection logic. This led to a silent data loss bug where
WebSocket messages larger than 64KB (common for MLS Welcome events) were truncated because
`EndOfMessage` was never checked. A dedicated library handles this correctly.

**Blockcore.Nostr.Client** (v2.0.2, NuGet) is a fork of Marfusios/nostr-client maintained by
the Blockcore team. It uses System.Reactive (IObservable), Websocket.Client (handles framing),
and NBitcoin.Secp256k1 — all dependencies OpenChat already uses. Actively maintained (Jan 2026).

## Goal

Add `Blockcore.Nostr.Client` as an alternative `INostrService` implementation, switchable via
CLI flag `--nostr blockcore|native` (default: `native` = current implementation).

## Architecture

Follow the existing `MdkBackend` pattern in `ProfileConfiguration`:

### 1. Add enum and config (ProfileConfiguration.cs)

```csharp
public enum NostrBackend { Native, Blockcore }

public static NostrBackend ActiveNostrBackend { get; private set; } = NostrBackend.Native;
public static void SetNostrBackend(NostrBackend backend) => ActiveNostrBackend = backend;
```

### 2. Parse CLI flag (Program.cs / Desktop entry point)

Same pattern as `--mdk managed|rust` parsing — add `--nostr blockcore|native`.

### 3. Create BlockcoreNostrService : INostrService

New file: `src/OpenChat.Core/Services/BlockcoreNostrService.cs`

This wraps `Blockcore.Nostr.Client` and implements `INostrService`. Key mappings:

| INostrService method | Blockcore.Nostr.Client equivalent |
|---|---|
| `ConnectAsync(relayUrls)` | `NostrMultiWebsocketClient` with `NostrWebsocketCommunicator` per relay |
| `Events` (IObservable) | `client.Streams.EventStream` → map to `NostrEventReceived` |
| `SubscribeToWelcomesAsync` | `client.Send(new NostrRequest(...))` with kind 444 filter |
| `SubscribeToGroupMessagesAsync` | `client.Send(new NostrRequest(...))` with kind 445 filter |
| `PublishKeyPackageAsync` | Build `NostrEvent`, sign, `client.Send(...)` |
| `PublishWelcomeAsync` | Build `NostrEvent`, sign, `client.Send(...)` |
| `PublishGroupMessageAsync` | Build `NostrEvent`, sign, `client.Send(...)` |
| `FetchKeyPackagesAsync` | Subscribe + collect until EOSE + unsubscribe |
| `FetchWelcomeEventsAsync` | Subscribe + collect until EOSE + unsubscribe |
| `FetchUserMetadataAsync` | Subscribe + collect until EOSE + unsubscribe |
| `FetchRelayListAsync` | Subscribe + collect until EOSE + unsubscribe |
| `GenerateKeyPair` / `ImportPrivateKey` | `NostrPrivateKey` / `NostrPublicKey` |
| `EncryptNip44` / `DecryptNip44` | Built-in NIP-44 support |
| `SetExternalSigner` | Custom adapter needed |
| `DisconnectAsync` | Dispose communicators |
| `ReconnectRelayAsync` | `communicator.Reconnect()` |
| `ConnectionStatus` | `communicator.ReconnectionHappened` + `DisconnectionHappened` |
| `WelcomeMessages` / `GroupMessages` | Filter `EventStream` by kind, map to domain types |

### 4. Swap in composition roots

**Desktop** (`App.axaml.cs`):
```csharp
INostrService nostrService = ProfileConfiguration.ActiveNostrBackend == NostrBackend.Blockcore
    ? new BlockcoreNostrService()
    : new NostrService();
```

**Android** (`MainActivity.cs`): Same pattern (Android always uses native for now, or make configurable).

## Key considerations

- **Newtonsoft.Json**: Blockcore.Nostr.Client uses Newtonsoft, we use System.Text.Json.
  This is a transitive dependency, not a code conflict — both can coexist.
- **External signer (NIP-46)**: The library doesn't have built-in NIP-46 support.
  The `SetExternalSigner` method needs a custom adapter that intercepts signing calls
  and delegates to `IExternalSigner.SignEventAsync`.
- **Fetch methods**: The library doesn't have a "fetch and return" pattern.
  Implement as: open temporary subscription → collect events → wait for EOSE → return.
  Use `Observable.TakeUntil(eoseSignal).ToList().Timeout(10s)`.
- **MIP event types**: kinds 443/444/445 are not standard Nostr kinds. The library's
  generic `NostrEvent` handles any kind — just filter by `event.Kind` and map tags.
- **Auto-reconnect**: Built into `NostrWebsocketCommunicator` (configurable timeout).
  Must re-send subscriptions on reconnect via `ReconnectionHappened` stream.

## Package to add

```xml
<PackageReference Include="Blockcore.Nostr.Client" Version="2.0.2" />
```

Add to `OpenChat.Core.csproj`.

## Testing

- All existing tests should pass with both backends (they use `INostrService` interface)
- Add integration test that connects to a real relay with both backends and verifies
  event round-trip (publish kind 1, subscribe, verify receipt)
- The WebSocket framing bug test: publish a >64KB event and verify it's received intact

## IoC note

IoC (Microsoft.Extensions.DependencyInjection) was evaluated and deemed unnecessary.
With only 7 services and 2 composition roots, the current manual wiring + interface
abstraction provides the same swappability without added complexity. The existing
MdkBackend pattern is the right approach.

## Estimated effort

- ~2-3 days for full implementation
- Start with connect/subscribe/receive (read path) — this is where most value is
- Add publish (write path) second
- External signer adapter last (most complex)
