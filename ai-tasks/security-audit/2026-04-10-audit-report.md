# Security Audit Report - 2026-04-10

**Auditor**: Codex
**Scope**: OpenChat app security review across relay networking, external signer flows, media handling, platform launch surfaces, storage, and native interop
**Status**: Findings documented

## Summary

This review focused on remotely reachable trust boundaries first: Nostr relay input, NIP-46 external signer connection flows, media download paths, and message parsing.

A second pass expanded the review into profile metadata handling, Android platform defaults, and production logging behavior. The findings below reflect both passes.

### Findings

1. **P1**: External signer relays are connected without scheme or SSRF validation
2. **P1**: Relay messages are buffered without an upper bound
3. **P2**: NostrConnect secrets are written to application logs
4. **P1**: Avatar metadata fetches bypass URL validation and streaming size limits
5. **P2**: Android backups remain enabled for plaintext local chat data
6. **P2**: Release builds default to debug logging and persist sensitive protocol traces

## Finding 1 - External signer relays are connected without scheme or SSRF validation

**Severity**: P1

### Impact

The NIP-46 flow accepts relay URLs from `bunker://` / `nostrconnect://` strings and from the signer relay input, then connects directly with `ClientWebSocket.ConnectAsync(new Uri(...))`.

Unlike standard relay connections, this path does not reuse the existing relay validation logic that enforces secure schemes and blocks private or reserved IP ranges. A malicious QR code, bunker string, or persisted session can therefore:

- downgrade the connection to insecure `ws://`
- force connections to localhost or private-network services
- persist a bad relay and reconnect to it automatically on later launches

### Evidence

- `src/OpenChat.Core/Services/ExternalSignerService.cs:60`
- `src/OpenChat.Core/Services/ExternalSignerService.cs:152`
- `src/OpenChat.Core/Services/ExternalSignerService.cs:274`
- `src/OpenChat.Core/Services/NostrService.cs:2774`

### Recommended remediation

- validate all external signer relay URLs through the same policy as normal Nostr relays before connecting
- reject `ws://` except for explicitly allowed local-development scenarios
- reject hostnames that resolve to private, loopback, link-local, or otherwise reserved addresses
- apply the same validation during session restore and reconnect paths, not just on first connect

### Suggested tasks

- [ ] Add a shared relay-validation helper for both `NostrService` and `ExternalSignerService`
- [ ] Block invalid signer relay URLs in `ConnectWithStringAsync`
- [ ] Block invalid signer relay URLs in `GenerateAndListenForConnectionAsync`
- [ ] Block invalid signer relay URLs in `RestoreSessionAsync`
- [ ] Add regression tests for `ws://`, `localhost`, `127.0.0.1`, and valid `wss://` inputs

## Finding 2 - Relay messages are buffered without an upper bound

**Severity**: P1

### Impact

Relay messages are accumulated into a `MemoryStream` until `EndOfMessage` without any maximum size check. A malicious or compromised relay can send a very large message and force the client to allocate unbounded memory before parsing JSON.

This creates a remotely triggerable denial-of-service condition that can terminate the app or make it unstable under memory pressure.

The issue exists in both the persistent relay connection path and the temporary relay fetch paths.

### Evidence

- `src/OpenChat.Core/Services/NostrRelayConnection.cs:408`
- `src/OpenChat.Core/Services/NostrService.cs:290`
- `src/OpenChat.Core/Services/NostrService.cs:1714`
- `src/OpenChat.Core/Services/NostrService.cs:1917`
- `src/OpenChat.Core/Services/NostrService.cs:2048`
- `src/OpenChat.Core/Services/NostrService.cs:2207`
- `src/OpenChat.Core/Services/NostrService.cs:2396`

### Recommended remediation

- impose a hard maximum message size before continuing to buffer frames
- close the relay connection when the limit is exceeded
- use different ceilings if needed for normal events versus known large payload classes, but keep them bounded
- add tests for oversized single-frame and multi-frame messages

### Suggested tasks

- [ ] Introduce a shared maximum WebSocket message size constant
- [ ] Enforce the limit in both `ReceiveFullMessageAsync` implementations
- [ ] Abort or drop connections that exceed the limit
- [ ] Add tests proving oversized messages are rejected without large allocations

## Finding 3 - NostrConnect secrets are written to application logs

**Severity**: P2

### Impact

The app logs the full `nostrconnect://...&secret=...` URI when generating a signer connection link. That secret is the bearer secret used to complete the NIP-46 handshake.

Because the app also exposes logs in the built-in log viewer, anyone with access to local logs during that period can recover the secret and replay the pending connection flow.

### Evidence

- `src/OpenChat.Core/Services/ExternalSignerService.cs:263`
- `src/OpenChat.Presentation/ViewModels/SettingsViewModel.cs:113`

### Recommended remediation

- never log full connection URIs that contain secrets
- log only redacted metadata such as relay host and local pubkey prefix
- review related signer and auth logging to ensure secrets and signed payloads are not emitted verbatim

### Suggested tasks

- [ ] Remove the full URI from `GenerateConnectionUri` logs
- [ ] Replace it with a redacted log entry
- [ ] Audit neighboring signer logs for similar secret exposure
- [ ] Add a regression test that asserts the generated URI secret is not written to logs

## Finding 4 - Avatar metadata fetches bypass URL validation and streaming size limits

**Severity**: P1

### Impact

Profile `picture` URLs from relay metadata are fetched directly with `HttpClient` instead of the hardened media download path.

That means a remote user can publish an avatar URL that causes the client to:

- connect to internal hosts or private-network services
- follow an arbitrary remote URL outside the app's normal media validation policy
- buffer a large response in memory before the size check is applied

This is especially relevant because profile fetching is triggered automatically while caching sender metadata and chat list avatars.

### Evidence

- `src/OpenChat.Core/Services/MessageService.cs:1925`
- `src/OpenChat.Core/Services/MessageService.cs:1944`
- `src/OpenChat.Presentation/ViewModels/ChatListViewModel.cs:525`
- `src/OpenChat.Presentation/ViewModels/ChatListViewModel.cs:529`
- `src/OpenChat.Core/Services/MediaDownloadService.cs:138`

### Recommended remediation

- route avatar downloads through the same URL validation logic used for media downloads
- reject non-HTTPS URLs and private or reserved IP destinations
- enforce size limits while streaming, before buffering the full body in memory
- consider disabling redirects or re-validating the final destination after redirect

### Suggested tasks

- [ ] Add a shared avatar download helper that reuses the hardened media URL validation policy
- [ ] Remove direct `HttpClient.GetAsync` and `GetByteArrayAsync` avatar fetches
- [ ] Stream avatar responses with a hard byte cap
- [ ] Add regression tests for `localhost`, private IPs, and oversized avatar responses

## Finding 5 - Android backups remain enabled for plaintext local chat data

**Severity**: P2

### Impact

The Android manifest enables OS backups with `android:allowBackup="true"` and there are no visible backup exclusion rules in the project.

At the same time, the app persists plaintext local user data, including:

- decrypted message bodies in the SQLite `Messages.Content` column
- decrypted media attachments in the on-disk media cache
- cached avatars and other profile-derived local artifacts

For an end-to-end encrypted messenger, allowing device-transfer or cloud backup of those plaintext stores weakens the expected confidentiality model.

### Evidence

- `src/OpenChat.Android/Properties/AndroidManifest.xml:3`
- `src/OpenChat.Android/Properties/AndroidManifest.xml:5`
- `src/OpenChat.Core/Services/StorageService.cs:116`
- `src/OpenChat.Core/Services/MediaCacheService.cs:8`
- `src/OpenChat.Core/Services/MediaCacheService.cs:60`

### Recommended remediation

- disable Android backups entirely for this app, or
- add explicit backup rules that exclude message databases, logs, media cache, and other sensitive local data

### Suggested tasks

- [ ] Set `android:allowBackup="false"` unless product requirements explicitly need backup
- [ ] If backup must remain enabled, add explicit backup-exclusion rules for sensitive app data
- [ ] Document the intended backup model for security review and release decisions

## Finding 6 - Release builds default to debug logging and persist sensitive protocol traces

**Severity**: P2

### Impact

`LoggingConfiguration.Initialize()` defaults the minimum log level to `Debug`, and both desktop and Android startup paths use that default without overriding it for production.

In this codebase, several debug traces include protocol-sensitive material such as:

- decrypted NIP-46 payload previews
- raw native MLS / Nostr JSON fragments
- detailed request and response traces that are useful during development but too revealing for normal user runs

Because logs are persisted to disk and exposed through the in-app log viewer and Android export flow, this is a practical local disclosure issue rather than a purely diagnostic concern.

### Evidence

- `src/OpenChat.Core/Logging/LoggingConfiguration.cs:35`
- `src/OpenChat.Desktop/App.axaml.cs:35`
- `src/OpenChat.Android/MainActivity.cs:42`
- `src/OpenChat.Core/Services/ExternalSignerService.cs:797`
- `src/OpenChat.Core/Services/ExternalSignerService.cs:803`
- `src/OpenChat.Core/Marmot/MarmotWrapper.cs:245`
- `src/OpenChat.Android/Fragments/SettingsFragment.cs:503`

### Recommended remediation

- default production builds to `Information` or higher
- keep verbose protocol tracing behind explicit developer-only switches
- audit existing debug log statements that include decrypted content, protocol payloads, or sensitive metadata

### Suggested tasks

- [ ] Change the default production log level from `Debug` to `Information`
- [ ] Gate verbose protocol logging behind a developer setting or debug-build-only flag
- [ ] Remove or redact decrypted payload previews from logs
- [ ] Add tests or release checks to prevent sensitive debug logging from shipping by default

## Notes

- The media download caller validates URLs before use, which reduces SSRF exposure in the current flow.
- The underlying download service still depends on callers remembering to validate first, so centralizing enforcement inside the service would be a worthwhile future hardening step.
- No code changes were made as part of this audit report; this file documents findings only.
