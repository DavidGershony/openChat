# C# MLS fixes plan (May 2026)

Companion to `whitenoise-mdk-divergence-2026-05.md`. That document analyses the
upstream MDK / Whitenoise drift. This document is the C#-side engineering
plan: what to change, in what order, and why we are starting in C# rather
than bumping the Rust MDK pin first.

## Status (live)

| Fix | State | Commit |
|---|---|---|
| B — Stop swallowing decrypt failures | **Done** | `b483de3` |
| A — NIP-01 rumor id (managed path) | **Done** | `933eb9c` |
| C — Widen KeyPackage extensions | **Deferred — see Correction below** | — |
| D — RequiredCapabilities at group creation | **Deferred — see Correction below** | — |
| MDK pin bump | Not started | — |
| E — Map MDK error variants | Deferred | — |
| F — Empty admin pubkeys | Open question | — |
| G — Stale generated FFI bindings | Cleanup, low priority | — |

**Currently waiting for:** human-run diagnostics against rebuilt WN-master
container, to read the new log lines from Fix B and confirm or refute the
remaining diagnoses.

## Architecture recap

Scramble has two parallel `IMlsService` backends:

- **Native path** — `Services/MlsService.cs` → `Marmot/MarmotWrapper.cs` →
  `Marmot/MarmotInterop.cs` (hand-rolled `[LibraryImport]`) →
  `scramble_native.dll` (Rust crate at `src/Scramble.Native`, consuming MDK
  via Cargo).
- **Managed path** — `Services/ManagedMlsService.cs` →
  `lib/marmot-cs` (`MarmotCs.Core.Mdk`) → `lib/dotnet-mls` (pure-C# RFC 9420
  MLS implementation).

Both paths satisfy `IMlsService` and `MessageService.cs` uses whichever is
DI-registered.

## Ordering rationale (why C# first, not Rust first)

The earlier divergence document recommended bumping MDK first as a "cheap
drift probe." That is wrong for this codebase, for three reasons:

1. **The Cargo pin isn't a real API audit.** `src/Scramble.Native/Cargo.toml`
   pins MDK as `branch = "master"`; the actual rev is in `Cargo.lock`. The
   bump is `cargo update` plus fallout — not a structured pass over the API
   surface.
2. **There are pre-existing C# bugs that exist regardless of the drift.**
   Fixes A and B were correctness bugs that MDK 0.7.1's leniency was hiding.
   They wanted fixing on their own merits.
3. **The Rust path is most useful as a differential oracle.** With the
   managed path fixed and the native path still on MDK 0.7.1, the diff
   between the two backends becomes a precise read on what MDK 0.7.1→0.8.0
   actually changes. That's only possible if both paths aren't broken in the
   same place for the same reason.

Original order: **B → A → C → D → bump MDK → revisit E → cleanup F/G**.
After Fixes A + B landed, see the *Correction* section below — Fixes C/D
are no longer next.

## Fixes

### Fix B — Stop swallowing commit-decrypt failures (DONE, commit `b483de3`)

**File:** `src/Scramble.Core/Services/MessageService.cs`

The previous catch matched any exception whose message contained
`"Failed to extract MLS message"`, `"UnprocessableResult"`, or `"stale"`.
That masked rumor-verify failures (MDK PR #287) and any other Unprocessable
result, which is why diagnostic tests failed with 15 s timeouts rather than
visible errors.

Replaced with a narrow predicate `IsExpectedPreJoinCommitFailure` that
requires **both** `"UnprocessableResult"` and `"epoch"` in the exception
message; everything else is logged at `LogError` with exception type, group
epoch, and ciphertext prefix.

Pure C#. Zero protocol semantics change. No regressions in unit tests.

### Fix A — Compute proper NIP-01 rumor id on the managed path (DONE, commit `933eb9c`)

**File:** `src/Scramble.Core/Services/ManagedMlsService.cs`

Replaced `Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")` rumor
id at lines 499 (kind:9) and 548 (kind:7) with
`SHA256(JSON.stringify([0,pubkey,created_at,kind,tags,content]))` via a
private `ComputeRumorEventId` helper that reuses
`NostrService.SerializeForEventId`.

This makes outbound MIP-03 rumors verify-id-clean against MDK 0.8.0
(PR #287). Also makes `LastEncryptedRumorEventId` match the value the
receiver computes, so reaction/reply "e"-tag references stay correlatable
across the wire.

**Out of scope (deliberately deferred):** the synthetic-rumor `id` at
`MarmotWrapper.cs:306` is on the *native* path. Scope was managed-only; the
bug only triggers after the MDK Rust pin bumps to 0.8.0 anyway.

Pure C#. No regressions in `Scramble.Core.Tests`, `Scramble.UI.Tests`, or
`MarmotCs.Core.Tests`.

### Correction: Fix C and Fix D were based on a misread of WN PR #791

After Fix A landed, before starting Fix C I read the actual diff of
[whitenoise-rs PR #791](https://github.com/marmot-protocol/whitenoise-rs/pull/791).
The original "WN injects GCE upgrade commits into legacy groups" reading
was wrong.

What PR #791 actually does:

- Adds *opt-in* admin APIs `Whitenoise::group_capability_upgrade_status`
  and `Whitenoise::upgrade_group_required_proposals`.
- Adds mirror types `GroupCapabilityUpgradeStatus`,
  `RequiredProposalUpgradeStatus`, `RequiredProposalUpgradability`.
- Adds `CapabilityUpgradeBlocked { proposal, blockers }` error variant.
- Adds an integration scenario `UpgradeRequiredProposalsAfterLegacySelfUpdate`
  that exercises the new admin API end-to-end.
- Adds a per-process `MDK_STORAGE_INIT_LOCK` to serialize MDK SQLite
  initialisation across concurrent MDK creations.

What PR #791 does **not** do:

- It does NOT change WN's default behaviour against legacy groups. WN does
  not automatically inject `GroupContextExtensions` upgrade commits into
  Scramble-created groups when those groups receive messages.
- An admin must explicitly call the new upgrade API for any GCE upgrade
  commit to be sent.
- Diagnostic tests in this repo do not call that API.

Implications for Fixes C and D:

- **Fix C (widen `supportedExtensionTypes`)** has no concrete failure to
  fix. The validator at
  `lib/dotnet-mls/src/DotnetMls/Group/MlsGroup.cs:1947-1949` already exempts
  `RequiredCapabilities` (0x0003) and `ExternalSenders` (0x0005) from the
  per-leaf check. Adding 0x0003 to `supportedExtensionTypes` is a no-op for
  the validator.
- **Fix D (advertise updated `RequiredCapabilities` at create)** has the
  same problem. Without WN treating Scramble groups as "legacy" in any
  default code path, advertising different capabilities at create-time
  doesn't change anything observable.

Both fixes are **deferred pending evidence** that WN actually rejects or
mutates groups based on advertised capabilities in a default code path. The
4 "membership not found" / "filter not matched" / "value is null" failures
in the diagnostic suite need a different root-cause hypothesis.

### Plausible remaining causes for the 4 group-interop failures

To investigate after Fix B's improved logging gives us evidence:

1. **MDK #287 verify_id on inbound *commit* messages.** Fix A only fixed
   outbound application/reaction rumors. Commits constructed by
   `ManagedMlsService` and sent through `EncryptCommitAsync` /
   `AddMemberAsync` may have rumor-id problems on the inbound side too. WN's
   MDK 0.8.0 would silently drop them, leaving Scramble's view of the group
   diverged from WN's. This would manifest as exactly the "WN doesn't see
   the new member" symptom.
2. **Some other change in the 21-commit MDK 0.7.1 → 0.8.0 gap** that the
   divergence analysis didn't surface. The original analysis focused on
   PR #287, PR #266, and the upgrade-API plumbing, but 21 commits is a lot
   and there may be a behaviour change in commit-processing or KeyPackage
   format negotiation that we missed.
3. **WN container readiness race.** `WhitenoiseDockerClient.EnsureRunningAsync`
   only checks `State.Running`; WN's SQLCipher cold-start can leave
   `wnd.sock` not yet listening when the first command runs. Exacerbated by
   PR #791's new `MDK_STORAGE_INIT_LOCK` serialising init under load. Could
   cause first-command races that look like "Filter not matched" or
   "Value is null".
4. **Empty admin pubkey list (Fix F below).**

Fix B's improved logging will, on the next diagnostic run, surface the
underlying exception type and `UnprocessableResult.Reason` for every
silently-discarded failure. Pick the next fix from the evidence rather than
from the divergence analysis.

### Fix C — Widen `supportedExtensionTypes` in KeyPackages — DEFERRED

**File:** `src/Scramble.Core/Services/ManagedMlsService.cs:239`

**Status:** deferred pending evidence. See *Correction* above. The original
rationale (WN PR #791 injects GCE upgrade commits requiring this) was wrong.

**If reactivated:** the validator only requires per-leaf advertisement for
extension types *other than* `RequiredCapabilities` and `ExternalSenders`.
Only widen if a specific extension type appears in an inbound GCE commit
that we want to apply.

### Fix D — Set initial RequiredCapabilities at group creation — DEFERRED

**Files:**
- `src/Scramble.Core/Services/ManagedMlsService.cs:270-299` (CreateGroupAsync)
- `lib/marmot-cs/src/MarmotCs.Core/Mdk.cs` (CreateGroupAsync — would need new param)

**Status:** deferred pending evidence. See *Correction* above. The original
rationale (prevent WN from classifying Scramble groups as legacy) was wrong:
WN's default code paths don't classify-and-act on this.

**If reactivated:** would require the marmot-cs submodule to expose a
`RequiredCapabilities` parameter on `CreateGroupAsync`.

### Bump MDK pin (after evidence-driven fixes)

**Files:**
- `src/Scramble.Native/Cargo.toml:14-15` — currently `branch = "master"`
- `src/Scramble.Native/Cargo.lock` — currently rev `0babfa76`

**Change:** `cargo update -p mdk-core -p mdk-memory-storage` to pull at least
`7f809f85` (v0.8.0). Then fix any FFI compile errors in
`src/Scramble.Native/src/client.rs` and `error.rs`.

This unblocks the native path against current WN. The C# changes from A/B
fix the managed path independently; the bump fixes the native path.

**Effort:** unknown — depends on FFI breakage. Estimate ~half a day if the
breakage is just renames/signature tweaks.

### Fix E — Map MDK error variants (defer)

**Files:**
- `src/Scramble.Native/src/error.rs` — add 5 variants
- `src/Scramble.Native/src/client.rs` — propagate them
- `src/Scramble.Core/Marmot/MarmotInterop.cs` — possibly new structured-error
  entry point (current path is `marmot_get_last_error()` → string)
- `src/Scramble.Core/Marmot/MarmotWrapper.cs:702-717`
- `lib/marmot-cs/src/MarmotCs.Core/Errors/MdkException.cs` — 5 new exception
  subclasses
- `src/Scramble.Core/Models/MlsDecryptionError.cs` — add a `Code` field

**Variants to map:** `NotAdmin`, `EmptyUpgradeSet`,
`ProposalNotInSupportedSet`, `ProposalAlreadyRequired`,
`ProposalNotAvailableForUpgrade`. (Per PR #791's
`CapabilityUpgradeBlocked` and the upgrade-API surface.)

**Why defer:** purely a diagnostics-quality improvement, and only relevant
if Scramble starts calling the upgrade APIs from PR #791 — which is not
planned. Touching it requires changes across Rust + FFI + C# + a submodule
for value of "better error messages" on a code path we don't exercise. Do
this only if/when we adopt the upgrade APIs.

### Fix F — `MlsService.GetAdminPubkeys` returns empty list (investigate)

**File:** `src/Scramble.Core/Services/MlsService.cs:309-313`

Returns `new List<string>()` unconditionally. `MessageService.cs:530-532`
falls back to creator-only admins, so today this is harmless. But it may be
relevant to "Filter not matched" symptoms in
`WhitenoiseGroupInteropTests.GroupChat_*` tests #3 and #4 if WN's group
view includes an admin-set check. Now a candidate cause for the remaining
4 failures — verify after the next diagnostic run.

### Fix G — Stale generated FFI bindings

**File:** `src/Scramble.Core/Marmot/Generated/MarmotNative.g.cs`

Auto-generated by csbindgen, sits unused next to the hand-rolled
`MarmotInterop.cs`. Drift hazard. Either delete it or switch the wrapper to
use it. Cleanup task.

## What to do next

1. **Run the diagnostic suite** against the rebuilt WN-master container.
   Read the new `LogError` lines from Fix B for each failing test.
2. **Triage each failure by exception type and reason.** Group them: which
   are still "WN sees nothing" (likely more verify_id), which are
   "Scramble sees nothing" (likely commit-side processing), which are
   "container race" (early-test-only).
3. **Pick the next fix from the evidence**, not from the original
   divergence analysis. Likely candidates: outbound commit rumor-id (Fix A
   extended to commits), readiness wait for `wnd.sock` (improving
   `WhitenoiseDockerClient.EnsureRunningAsync`), Fix F.
4. **Bump MDK pin** when the managed path is fully green so the native
   path can catch up using the same backend that the C# code is now
   verified against.

## Out-of-scope reminders

- MLS wire formats are unchanged.
- KeyPackage kinds (30443/443), Welcome (444), MIP-03 (445), exporter secret
  derivation are unchanged.
- WN CLI surface used by `WhitenoiseDockerClient.cs` is unchanged since the
  2026-04-24 alignment.
- `tests/whitenoise-docker/Dockerfile` already pulls latest WN master via
  `ARG WHITENOISE_REF=master` + `ARG CACHEBUST` — no further test infra work
  needed for this plan.

## CLAUDE.md compliance

None of these fixes are UI features, so the
`Scramble.Android` + `Scramble.UI` parity rule does not apply. All edits land
in `Scramble.Core`, `Scramble.Native`, or the `lib/*` submodules.
