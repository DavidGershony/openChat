# C# MLS fixes plan (May 2026)

Companion to `whitenoise-mdk-divergence-2026-05.md`. That document analyses the
upstream MDK / Whitenoise drift. This document is the C#-side engineering
plan: what to change, in what order, and why we are starting in C# rather
than bumping the Rust MDK pin first.

## Architecture recap

Scramble has two parallel `IMlsService` backends:

- **Native path** — `Services/MlsService.cs` → `Marmot/MarmotWrapper.cs` →
  `Marmot/MarmotInterop.cs` (hand-rolled `[LibraryImport]`) →
  `scramble_native.dll` (Rust crate at `src/Scramble.Native`, consuming MDK
  via Cargo).
- **Managed path** — `Services/ManagedMlsService.cs` →
  `lib/marmot-cs` (`MarmotCs.Core.Mdk`) → `lib/dotnet-mls` (pure-C# RFC 9420
  MLS implementation).

Both paths satisfy `IMlsService` and `MessageService.cs` uses whichever is DI-
registered. The managed path has full GCE-proposal support already in
`lib/dotnet-mls/src/DotnetMls/Group/MlsGroup.cs` (lines 311, 707-755, 1869,
1941-1958); what's missing is just plumbing it up through marmot-cs and
Scramble.Core.

## Ordering rationale (why C# first, not Rust first)

The earlier divergence document recommended bumping MDK first as a "cheap
drift probe." That is wrong for this codebase, for three reasons:

1. **The Cargo pin isn't a real API audit.** `src/Scramble.Native/Cargo.toml`
   pins MDK as `branch = "master"`; the actual rev is in `Cargo.lock`. The
   bump is `cargo update` plus fallout — not a structured pass over the API
   surface.
2. **There are pre-existing C# bugs that exist regardless of the drift.**
   Fixes A and B below are correctness bugs that MDK 0.7.1's leniency was
   hiding. They want fixing on their own merits.
3. **The Rust path is most useful as a differential oracle.** With the
   managed path fixed and the native path still on MDK 0.7.1, the diff
   between the two backends becomes a precise read on what MDK 0.7.1→0.8.0
   actually changes. That's only possible if both paths aren't broken in the
   same place for the same reason.

Order: **B → A → C → D → bump MDK → revisit E → cleanup F/G**.

## Fixes

### Fix B — Stop swallowing commit-decrypt failures (do first)

**File:** `src/Scramble.Core/Services/MessageService.cs:1220-1234`

Currently catches and silently discards exceptions whose messages contain
`"Failed to extract MLS message"`, `"UnprocessableResult"`, or `"stale"`. This
masks both rumor-verify failures and GCE-commit-rejection failures, which is
why the diagnostic tests fail with 15 s timeouts rather than visible errors.

**Change:** keep the swallow for genuinely benign cases (e.g. duplicate
delivery) but log the full exception at warning level with the kind:445 event
id, sender pubkey, group id, and exception type. Without this, no other fix
is verifiable.

**Effort:** ~30 min. Pure C#. No protocol semantics change.

### Fix A — Compute proper NIP-01 rumor id on the managed path

**Files:**
- `src/Scramble.Core/Services/ManagedMlsService.cs:499` (EncryptMessageAsync)
- `src/Scramble.Core/Services/ManagedMlsService.cs:548` (EncryptReactionAsync)
- `src/Scramble.Core/Marmot/MarmotWrapper.cs:306` (synthetic rumor in
  ProcessWelcomeAsync)

**Bug:** rumor `id` is built as
`Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")` — a 64-char
random hex string. NIP-01 requires
`id = SHA256(JSON.stringify([0,pubkey,created_at,kind,tags,content]))`. MDK
0.7.1 was lenient; MDK 0.8.0 enforces this via `verify_id()` and silently
drops mismatches. This is the proximate cause of the "WN sees Alice's
message" timeouts on the managed path.

**Helper already exists in the same file:**
- `BuildSignedNostrEventAsync` lines 1273-1302
- `BuildEphemeralSignedEvent` lines 1391-1393

Both compute a real NIP-01 id via the canonical `[0,pk,ts,kind,tags,content]`
JSON-serialise → SHA-256 path.

**Change:**
1. Extract a private `static string ComputeNip01EventId(string pubkeyHex, long createdAt, int kind, IEnumerable<string[]> tags, string content)` from the existing helpers.
2. Replace the two `Guid.NewGuid()` rumor-id sites at lines 499 and 548.
3. Replace the synthetic id at `MarmotWrapper.cs:306` (currently uses the
   *outer 1059 wrapper* event id, which is also wrong) with a NIP-01 id over
   the synthesised rumor's own fields. If the source kind:444 welcome doesn't
   carry enough fields to compute one, this site needs a different approach
   — either pass raw bytes through a different MDK API or accept that this
   path will only work with managed-side senders.

**Effort:** ~2-4 hours including tests. Pure C#. Correctness fix valid
against both MDK versions.

**Verification:** managed-path Scramble → WN-master kind:445 send should
succeed once this lands; can be tested today against the rebuilt WN docker
image without any Rust change.

### Fix C — Widen `supportedExtensionTypes` in KeyPackages

**File:** `src/Scramble.Core/Services/ManagedMlsService.cs:239`

**Current:** advertises only `{0x000A LastResort, 0xF2EE NostrGroupData}` in
`supportedExtensionTypes` when generating KeyPackages.

**Why it matters:** Whitenoise PR #791 injects `GroupContextExtensions` (GCE)
upgrade commits into groups that look "legacy." For the GCE proposal to
validate on the managed receive side,
`MlsGroup.ValidateGroupContextExtensions`
(`lib/dotnet-mls/src/DotnetMls/Group/MlsGroup.cs:1941-1958`) requires every
member's leaf to advertise the relevant extension types. Right now those
extensions aren't advertised, so even after the rumor-id fix, WN's GCE
commit will fail to apply on the managed side.

**Change:** add `0x0003 RequiredCapabilities` and any additional extension
types that WN's GCE upgrade carries. The `ExtensionType` enum already exists
in dotnet-mls (`Types/Extension.cs:8-15`).

**Effort:** ~30 min plus interop test. Pure C#. No submodule edit.

### Fix D — Set initial RequiredCapabilities at group creation

**Files:**
- `src/Scramble.Core/Services/ManagedMlsService.cs:270-299` (CreateGroupAsync)
- `lib/marmot-cs/src/MarmotCs.Core/Mdk.cs` (CreateGroupAsync — needs new param)

**Why it matters:** if Scramble advertises an up-to-date `RequiredCapabilities`
at group creation, WN won't classify the group as legacy and won't inject the
GCE upgrade commit at all. This is the preventive complement to Fix C.

**Change:** thread a `RequiredCapabilities` extension (and supported-proposal
list) through `Mdk.CreateGroupAsync` into the initial `GroupContext`.
dotnet-mls already supports this on `MlsGroup`; marmot-cs just needs the
parameter exposed.

**Effort:** ~half a day. Pure C# but touches the marmot-cs submodule.

**Caveat:** the precise capability values to advertise depend on which MDK
version's required-capabilities format we want to match. May want to do this
*after* the MDK bump so we know the correct values to send. Could also be
deferred indefinitely if Fix C is sufficient (reactive strategy works on its
own; preventive is belt-and-braces).

### Bump MDK pin (after C, possibly D)

**Files:**
- `src/Scramble.Native/Cargo.toml:14-15` — currently `branch = "master"`
- `src/Scramble.Native/Cargo.lock` — currently rev `0babfa76`

**Change:** `cargo update -p mdk-core -p mdk-memory-storage` to pull at least
`7f809f85` (v0.8.0). Then fix any FFI compile errors in
`src/Scramble.Native/src/client.rs` and `error.rs`.

This unblocks the native path against current WN. The C# changes from A/B/C
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
`ProposalNotAvailableForUpgrade`.

**Why defer:** purely a diagnostics-quality improvement. The current
string-based `MarmotException` keeps working; nothing test-failing depends on
this. Touching it requires changes across Rust + FFI + C# + a submodule, and
the value is "better error messages." Do this once everything else is green.

### Fix F — `MlsService.GetAdminPubkeys` returns empty list (investigate)

**File:** `src/Scramble.Core/Services/MlsService.cs:309-313`

Returns `new List<string>()` unconditionally. `MessageService.cs:530-532`
falls back to creator-only admins, so today this is harmless. But it may be
relevant to "Filter not matched" symptoms in
`WhitenoiseGroupInteropTests.GroupChat_*` tests #3 and #4 if the filter is
joined against the admin set. Verify after A/B/C land.

### Fix G — Stale generated FFI bindings

**File:** `src/Scramble.Core/Marmot/Generated/MarmotNative.g.cs`

Auto-generated by csbindgen, sits unused next to the hand-rolled
`MarmotInterop.cs`. Drift hazard. Either delete it or switch the wrapper to
use it. Cleanup task.

## Sequenced plan

| # | Fix | Path | Effort | Verifiable how |
|---|---|---|---|---|
| 1 | B (logging) | C# only | 30 min | Re-run failing diagnostics; failures should now produce log lines instead of timeouts |
| 2 | A (rumor id) | C# only, managed path | 2-4 h | Managed-path Scramble→WN kind:445 sends start being received |
| 3 | C (KeyPackage caps) | C# only, managed path | 30 min + test | WN-injected GCE commits now validate on managed receive |
| 4 | D (RequiredCapabilities at create) | C# + lib/marmot-cs | ~half day, optional | WN no longer injects GCE upgrade commits into Scramble-created groups |
| 5 | MDK pin bump | Rust | ~half day + fallout | Native-path diagnostic tests fixed |
| 6 | E (error variants) | Rust + C# + submodule | 1+ day, defer | Diagnostics polish |
| 7 | F, G | C# cleanup | small | Cleanup |

After step 3, expect the managed-path interop tests to pass. After step 5,
expect the native-path interop tests to pass. The gap between steps 3 and 5
is the differential window where the Rust path serves as a controlled-
variable reproduction harness.

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
