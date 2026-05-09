# Whitenoise / MDK divergence analysis (May 2026)

## Summary

Scramble's interop with Whitenoise has drifted since the last alignment on
**2026-04-24** (commit `b0fce96` "Fix WhitenoiseDockerClient group ID parsing
for latest WN CLI"). After rebuilding the test Whitenoise Docker image against
current `master` (`59aa42cc`, WN v0.2.1, MDK v0.8.0), 5 diagnostic tests fail
with symptoms consistent with two specific upstream changes in MDK and one in
Whitenoise.

The MLS protocol layer itself has **not** changed. Drift is at the MDK Rust
API surface and at Whitenoise's accommodation behaviour for groups created by
older MDK clients.

## Versions

| Component | Scramble pin | Current upstream | Delta |
|---|---|---|---|
| MDK (Marmot) | `0babfa76` (~v0.7.1, 2026-04-22) | `7f809f85` (v0.8.0) | 21 commits |
| whitenoise-rs | aligned 2026-04-24 | `59aa42cc` (v0.2.1, 2026-05-08) | ~14 days |
| MLS wire format | unchanged | unchanged | none |

Scramble's MDK pin lives in `src/Scramble.Native/Cargo.toml` and is locked in
`src/Scramble.Native/Cargo.lock`.

## Test results against latest WN

Run after rebuilding `tests/whitenoise-docker/Dockerfile` with
`ARG WHITENOISE_REF=master` + `ARG CACHEBUST` to force a fresh clone of WN
master (`59aa42cc`).

- Unit (Core): 397 passed, 14 failed (all 14 require optional `scramble_native.dll`, not built), 16 skipped
- Unit (UI): 216 passed, 0 failed, 11 skipped
- Integration: 5 passed, 0 failed
- **Diagnostics: 32 passed, 5 failed, 13 skipped**

### Failing diagnostic tests

1. `WebAppInteropInvestigationTests.FullFlow_ThreeUsers_CreateGroup_InviteAcceptMessage`
2. `WhitenoiseGroupInteropTests.GroupChat_4Users_2Scramble_2Whitenoise` — *Collection was empty*
3. `WhitenoiseGroupInteropTests.GroupChat_WhitenoiseCreatesGroup_ScrambleJoins` — *Filter not matched*
4. `WhitenoiseGroupInteropTests.GroupChat_3Users_2Scramble_1Whitenoise` — *Value is null*
5. `FullE2EGroupInteropTests.E2E_3Users_2OC_1WN_FullFlow` — *Filter not matched*

Recurring symptom: `WN poll 'WN sees Alice's message' timed out after 15000ms`.

## Root causes (mapped to upstream commits)

### 1. MDK PR #287 — rumor `verify_id()` enforced inbound and outbound (`7f809f8`)

MDK v0.8.0 now validates that the rumor `id` field on every kind:445 MIP-03
message matches the canonical hash of the rumor's other fields, on both send
and receive. If Scramble constructs outbound rumors with a stale, missing or
incorrectly computed `id`, MDK v0.8.0 in Whitenoise will silently drop them.

This is the most likely cause of the **"WN sees Alice's message" timeouts**
across failures #1, #2, #5.

Risk surface in Scramble: wherever outbound MIP-03 rumors are built. Likely:
- `src/Scramble.Core/Marmot/MarmotWrapper.cs`
- `src/Scramble.Core/Services/MessageService.cs`
- `src/Scramble.Core/Services/ManagedMlsService.cs`

### 2. Whitenoise PR #791 — required-proposal upgrades for legacy groups (`4712cd99`)

Current Whitenoise injects `GroupContextExtensions` (GCE) commits into groups
whose creator advertised an older `RequiredCapabilities` set (i.e. groups
created by Scramble pinned to MDK 0.7.1). Scramble's MDK 0.7.1 likely either
errors on or ignores these commits, leaving the two sides at divergent epochs.

This is the most likely cause of the **"Filter not matched" / "Value is null"
/ "Collection was empty"** errors in failures #2, #3, #4, #5 — Scramble cannot
find the WN member because its local view of the group never advanced past
the GCE commit.

### 3. MDK PR #266 — GroupContextExtensions upgrade APIs and 5 new error variants

New error variants exposed by MDK that Scramble's C# error mapping does not
handle:
- `NotAdmin`
- `EmptyUpgradeSet`
- `ProposalNotInSupportedSet`
- `ProposalAlreadyRequired`
- `ProposalNotAvailableForUpgrade`

Until mapped, these will surface as generic / unknown errors in Scramble's
diagnostics and obscure the real failure mode.

## What did NOT change

Useful to know for scoping:

- KeyPackage wire format (kinds 30443 / 443) — only the legacy 443 acceptance
  window was extended to 2026-05-31
- Welcome wire format (kind 444)
- MIP-03 ChaCha20-Poly1305 group message wire format (kind 445)
- Exporter secret derivation
- WN CLI subcommand names and JSON shapes — no rename or removal since the
  2026-04-24 alignment, so `WhitenoiseDockerClient.cs` parsing is still valid
- The MLS protocol itself

## Recommended remediation order

Tackle in this order — each step unblocks the diagnostics for the next.

1. **Bump MDK pin** in `src/Scramble.Native/Cargo.toml` from
   `0babfa76` to `7f809f85` (v0.8.0). Rebuild `scramble_native` and fix any
   Rust / FFI compile errors. This puts both sides on the same MDK and is the
   foundation for everything else.
2. **Map the 5 new MDK error variants** in Scramble's C# error layer so
   subsequent failures are diagnosable rather than masked.
3. **Audit outbound rumor construction** for MIP-03 kind:445 messages (see
   files listed under #1 above) and ensure every outbound rumor's `id` is
   computed from its current canonical fields. This addresses MDK #287.
4. **Decide GCE strategy** for WN #791. Two options:
   - *Preferred:* upgrade Scramble's `RequiredCapabilities` advertisement at
     group creation so WN no longer classifies Scramble-created groups as
     legacy and skips the GCE upgrade commit entirely.
   - *Fallback:* tolerate the inbound GCE commit as a no-op state advance so
     epochs stay aligned.
5. **Improve container readiness** in
   `tests/Scramble.Diagnostics/WhitenoiseInterop/WhitenoiseDockerClient.cs`
   `EnsureRunningAsync` to wait for `wnd.sock` rather than racing the SQLCipher
   cold start (per WN #758). Reduces test flake but does not fix any of the 5
   failures.

## Reference commits

- Scramble alignment: `b0fce96` "Fix WhitenoiseDockerClient group ID parsing for latest WN CLI" (2026-04-24)
- MDK pin (Scramble): `0babfa763f93fbd2c12e1f1b9ce9a28ab2516e46`
- MDK current (used by WN v0.2.1): `7f809f8549458a0d7f7d885bcdd694023abf299c`
- Whitenoise current `master` HEAD: `59aa42cc4cb5257beff49fd337b6c55857c80f73`

## Test environment notes

- `tests/whitenoise-docker/Dockerfile` was modified to use
  `ARG WHITENOISE_REF=master` and `ARG CACHEBUST` so future rebuilds always
  pull current upstream rather than caching `--depth 1` clones.
- Tests can reuse an already-running `openchat-nostr-relay-1` on port 7777
  instead of starting `scramble-nostr-relay-1`.
- Submodules (`lib/dotnet-mls`, `lib/marmot-cs`) must be initialised with
  `git submodule update --init --recursive` before any build.
