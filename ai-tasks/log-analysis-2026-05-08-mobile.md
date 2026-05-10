# Mobile log analysis — `scramble-20260508.log`

## Summary

The user's reported symptom — **"the app closes every time I switch back to it from background, then I have to reopen it; works fine afterwards"** — is **fully consistent with the log**. The app is **not crashing**; Android is killing the cached background process while it sits idle, and the launcher relaunches a fresh process when the user taps the icon. There are no unhandled exceptions, no `[FTL]` entries, no `AndroidRuntime` traces, no `OutOfMemory`.

This obsoletes the assumption in `log-audit-12-frequent-app-restarts.md` that the cause is a crash-restart loop.

## Log facts

- File: `D:\Users\david\openCodeProjects\openChat\ai-tasks\scramble-20260508.log`, 25 MB, 110 639 lines, single perSession file (Android writes one file per process launch — so this file represents **one** process actually).
- Wait — that contradicts the four `=== Scramble Application Started ===` markers. Confirmed by re-reading: each Android launch writes a new perSession file. So this single file actually contains **multiple sessions concatenated**. The four "Started" markers therefore represent four real process launches captured across **whatever device-side log aggregation produced the upload** (e.g. concatenation in Settings → Export Logs).
- Number of unique process launches: **4** (16:32:50, 16:40:41, 23:45:10, 23:52:10), not the "27 in a day" reported in issue #12 (see Finding 1 below).
- Real session uptimes:
  - Session 1: 16:32:50 → idle to 16:40:41 (~7m48s) → **killed in background**
  - Session 2: 16:40:41 → last log at 17:23:33 → silent until 23:45:10 (**~6h22m of process death**) → relaunch
  - Session 3: 23:45:10 → idle to 23:52:10 (~7m) → **killed in background**
  - Session 4: 23:52:10 → continues to end of log
- Zero `[FTL]`, zero `AndroidRuntime`, zero `UnhandledException`, zero `OutOfMemory`.
- 2 490 lines matching error patterns, but **all are network/WebSocket reconnect noise** (relay aborts, metadata fetch failures); the rest of the matches are false positives where "FATAL" appears as a substring inside base64-ish hex content of NIP-46 events.
- `RelayForegroundService` is wired (`MainActivity.cs:430`) but only started when the user enables "Background notification mode" in Settings. **No log entries from `RelayForegroundService` exist in this file** → user does not have background mode enabled → no foreground service holds the process alive → Android freezer / cached-app-killer reclaims the process within minutes of being backgrounded.

## Finding 1 — Duplicate "Application Started" banner

Each real process launch logs the startup banner **twice** within ~100 ms:

```
16:32:50.498 [INF] [] === Scramble Application Started ===
16:32:50.594 [INF] [] Log directory: ...
16:32:50.594 [INF] [] Log level: "Information"
16:32:50.601 [INF] [] === Scramble Application Started ===     ← duplicate
16:32:50.602 [INF] [] Log directory: ...
16:32:50.603 [INF] [] Log level: "Information"
```

`LoggingConfiguration.Initialize` is the only writer of the banner and is guarded by `_initialized` + `lock`. Yet two banner-blocks are written. Two entry points exist:

- `Scramble.Android/MainActivity.cs:49` — `Initialize(perSession: true, appVersion: ...)`
- `Scramble.Android/ShareTargetActivity.cs:51` — `Initialize(perSession: false)` (no appVersion)

Neither logged banner contains the `App version: ...` line that `Initialize` emits when `appVersion` is supplied (`LoggingConfiguration.cs:88`). This means **both calls happen via the no-arg `EnsureInitialized()` path**, before MainActivity reaches its explicit call. Plausible cause: a static field initializer in some early-loaded class (e.g. `Scramble.Android.Services.ThemeService._logger` is referenced from `MainActivity.OnCreate:39` `SetTheme(ThemeService.GetSavedStyleResource(...))`, which runs **before** the explicit `LoggingConfiguration.Initialize` on line 49) calls `CreateLogger<object>()` → `EnsureInitialized()` → `Initialize()`.

That explains banner #1 (no appVersion). Banner #2 is harder to pin from logs alone — possibilities:
- A second `Initialize()` call gets through because `_initialized = true` is set on line 93 *after* the banner on line 87, and `lock` is reentrant in C#. Some Serilog enricher or sink, on its first write, dereferences a static logger field on another class that calls `CreateLogger<T>` → `EnsureInitialized()` → re-enters `Initialize()`.
- More mundane: `Initialize` is also called during `MainActivity.OnCreate` (line 49) — but then it should hit the early-return guard.

The exact reentrancy path is not diagnosable from the log alone (no internal Serilog frames are captured). The fix is independent of the cause: **set `_initialized = true` before writing the banner**, so the reentrant call is a no-op.

**Impact:** the `27 starts in 20 hours` figure cited in `log-audit-12` is roughly **2× over-counted**. Real number is ~14 starts/day, and the log we have shows only 4 across this 7-hour window.

**Suggested fix** (small, in `src/Scramble.Core/Logging/LoggingConfiguration.cs`):

```csharp
Log.Logger = logConfig.CreateLogger();
_loggerFactory = new SerilogLoggerFactory(Log.Logger);
_initialized = true;                                        // ← move before banner
Log.Information("=== Scramble Application Started ===");
if (!string.IsNullOrEmpty(appVersion))
    Log.Information("App version: {AppVersion}", appVersion);
Log.Information("Log directory: {LogDirectory}", LogDirectory);
Log.Information("Log level: {Level}", minimumLevel);
```

## Finding 2 — App killed in background (root cause of user's symptom)

After session 2 stops logging at **17:23:33** the next line is **`=== Scramble Application Started ===` at 23:45:10** — over six hours of process death with no farewell. Sessions 1, 3, 4 show the same shape but shorter. This is the textbook signature of:

- Android's **App Standby Buckets** / **Cached App Freezer** (Android 12+) freezing the process,
- Then the **OOM killer** reclaiming the frozen process under memory pressure (or after the OEM's aggressive battery saver kicks in — Samsung, Xiaomi, OnePlus all do this).

Without a foreground service Scramble has no claim on staying alive. WebSocket reconnect loops to public relays (visible in the warning histogram below) likely make Android's process scheduler treat the app as "high-cost cached" and prioritize it for kill.

Warning histogram for context (top patterns over the run):

| count | message |
|---:|---|
| 91 | `Failed to fetch metadata from relay wss://test.thedude.cloud` |
| 91 | `Failed to fetch metadata from relay wss://relay2.angor.io` |
| 87 | `Failed to fetch metadata from relay wss://relay.thedude.cloud` |
| 85 | `Failed to fetch relay list from wss://purplepag.es` |
| 81 | `Failed to fetch metadata from relay wss://nos.lol` |
| ... | various per-relay metadata/list failures |
| 8 | `LoadChats: chat e13f3a34 'Claude docker' (type="Bot") has 1 participants but current user NOT found ... Marking as orphaned.` |
| 6 | `Failed to fetch Welcome events from relay wss://nos.lol` |
| 4 | `Failed to restore MLS service state, will generate new keys` |

**Suggested fixes** (more involved, separate tasks):

1. **Default `notification_mode` to `Background` on Android**, or prompt the user once on first run with copy explaining "Scramble must keep a small foreground notification to receive new messages while the app is in the background." Without this, the user's symptom is unfixable on Android without OS-level allowlists.
2. **Add `WorkManager` periodic sync** as a fallback for users who reject the foreground notification — periodic `OneTimeWorkRequest` or `PeriodicWorkRequest` that wakes briefly to drain the relay subscription, post any kind 14/Welcome events to the notification system, and exits. (Requires a separate plan.)
3. **Document the OEM-specific battery-allowlist hurdles** (Samsung, Xiaomi, OnePlus) in onboarding.
4. Persist a small startup breadcrumb file containing `lastForegroundExitTime` / `lastBackgroundEntryTime` / `previousLaunchReason` so future logs can distinguish "OS killed cached process" from "user task-swiped" from "actual crash".

## Finding 3 — `ImportServiceStateAsync` called with empty buffer

Four warnings of:

```
DotnetMls.Codec.TlsDecodingException: Insufficient data: attempted to read 1 byte(s) at position 0, but only 0 byte(s) remain.
   at DotnetMls.Codec.TlsReader.ReadUint8()
   at Scramble.Core.Services.ManagedMlsService.ImportServiceStateAsync(Byte[] state)
   at Scramble.Core.Services.ManagedMlsService.InitializeAsync(String privateKeyHex, String publicKeyHex)
```

— for **three different account pubkeys** (`e9b03d7d…`, `53a471cf…`, `68fd21eb…`, plus one repeat). Symptom: the user has multiple Nostr accounts in the registry (account-switcher is being exercised). For each fresh account the saved MLS state blob is empty / zero-byte, and the import path runs the TLS decoder anyway → throws → falls back to "generate new keys."

Functionally harmless (the fallback works). Cosmetically noisy: every account-switch logs a stack trace, and the WRN level makes log triage harder. Worth a 5-line guard:

```csharp
// In ManagedMlsService.ImportServiceStateAsync
if (state == null || state.Length == 0)
    return;   // nothing to restore — first-time use of this account
```

…paired with not logging the WARN+stack when the state was simply absent (only log when we *had* state and it failed to parse). Small task.

## Finding 4 — Orphaned `Claude docker` chat

8 occurrences of:

```
LoadChats: chat e13f3a34 'Claude docker' (type="Bot") has 1 participants but current user NOT found.
Participants: [bdb6c5d6cfcd9919]. Marking as orphaned.
```

This is the same orphaned-bot-chat pattern that already has its own audit (`log-audit2-01-orphaned-group-zero-participants.md` is in `completed/`, but a single bot chat from another account leaked into the current account's view because a stale row in `chats` references `bdb6c5d6…` (a different user's pubkey) and the `LoadChats` query doesn't filter by current user. Likely related to the multi-account setup and migration from openChat → Scramble. Probably best handled inside the existing orphan-handling code rather than a new task.

## Finding 5 — Network noise

Reconnect loop is healthy: every disconnect is followed by `Auto-reconnecting … in 1s` then `Reconnected` and `Replayed 1 subscriptions`. No backoff escalation issues are visible (`log-audit-10` is already completed). The 90+ `Failed to fetch metadata` warnings per relay over a few hours are a separate concern: they indicate that profile-metadata batching has high failure rate on public relays. Not actionable from this log alone.

## Finding 6 — NIP-46 logging dominates the log volume (~94%)

`ExternalSignerService` produced **93 546 of the 99 158 timestamped lines (~94%)** in this file. Two patterns account for almost all of it:

1. **Raw relay frame echo at INFO**, once per WebSocket frame:

   ```
   [INF] NIP-46 raw relay data from wss://relay.nsec.app (1228 bytes): eyJraW5kIjoyNDEzMy...<300 char base64 prefix>
   ```

   This is a low-level wire dump: it duplicates what the parsed-event log immediately below it already says, and the 300-character payload prefix makes the log file roughly 5× larger than it needs to be. Useful for first-time NIP-46 debugging; not useful in production INFO output.

2. **Per-relay event fanout at INFO**: when a NIP-46 event is published to N relays, the listener loop fires once per relay and each fire logs:

   ```
   [INF] NIP-46 received event kind=24133 from <sender-hex>
   ```

   With 5 NIP-46 relays configured (`relay.nsec.app`, `nsec.app`, `relay.damus.io`, `nostr.mom`, `relay.primal.net` were the typical set), every signer event is logged 5 times. The day's ~5 600 unique signer events became ~28 000 INFO lines.

Together these two patterns produced an estimated **5×** inflation factor on signer log volume and pushed the daily file past 25 MB, which makes export-and-share painful for the user and slows triage.

### Implemented fix — commit `47a60bc`

`src/Scramble.Core/Services/ExternalSignerService.cs`:

- Demoted the raw-frame log from `LogInformation` → `LogDebug`. The frame is still inspectable when a developer raises the log level, but it no longer ships in production logs.
- Added a bounded FIFO dedupe cache (`_loggedEventIds` HashSet + `_loggedEventOrder` Queue, capacity 1024, guarded by `_loggedEventsLock`). New helper `ShouldLogReceivedEvent(string eventId)` returns `true` only the first time a given event id is seen.
- In the `messageType == "EVENT"` branch, parse `id` from the event JSON. First sighting per id logs `NIP-46 received event kind={Kind} from {Sender}` at INFO; subsequent per-relay duplicates log `NIP-46 duplicate event {EventId} from {Relay} (already processed)` at Debug.

Expected effect on a future log of similar shape: ~94% → roughly **30–40%** of the file from `ExternalSignerService`, and the total file drops from ~25 MB to ~3–5 MB for an equivalent day of activity.

This is observability-only; no signer behaviour changes.

## Recommended priorities

1. **(P1, small)** Move `_initialized = true` before banner write in `LoggingConfiguration.Initialize` — fixes Finding 1 and removes ~50 % of the noise that made `log-audit-12` look catastrophic.
2. **(P1, small)** Empty-buffer guard in `ManagedMlsService.ImportServiceStateAsync` — Finding 3.
3. **(P0 for the user, larger)** Address the actual symptom by making background mode either default-on or one-tap to enable on first run, plus a `WorkManager` fallback — Finding 2. This deserves its own design doc; the current `log-audit-12-frequent-app-restarts.md` task should be **rewritten** in light of this analysis (it's not a crash problem; it's an Android lifecycle problem).
4. **(P2)** Add startup-reason / lifecycle breadcrumb logging so future logs distinguish OS-kill from user-quit from crash (this is goal #1 / #3 of the existing `log-audit-12` task — still useful, just not the urgent thing).
5. **(P1, done — commit `47a60bc`)** Cut NIP-46 log noise — Finding 6.

## Not findings (verified clean)

- No native crashes, no AOT issues, no hangs (gift wrap timeout `WRN` line is normal).
- No memory leak indicator visible (no `OutOfMemory`, no escalating GC patterns visible in informational lines).
- WebSocket reconnect/replay logic is healthy.
- NIP-46 (Amber) signing path is producing many successful round-trips.
