# Multi-Device Phase 1 — Same Account on Laptop + Mobile

> **Companion to** `ai-tasks/mip06-multi-device-report.md` (Phase 2 / future).
> **Status:** Plan only — paused at user direction May 11 2026. **MIP-06 is
> explicitly NOT being pursued at this time** (user decision). The plan
> remains valid as the path forward when work resumes; the MIP-06 appendices
> are kept for reference but are not on the near-term roadmap.
> **Goal:** Ship a working "use Scramble on my laptop AND my phone under one identity"
> experience with no MDK API changes, no protocol changes, and no dependency on
> upstream MIP-06 merge.

## 0. Session log — context for whoever picks this up

This plan was written in one planning session on May 11 2026. The user asked
"plan out and suggest solutions for exporting keypackages so I can have the same
chat on my laptop and mobile." After clarifying that "export keypackages"
conflates four different problems (see §1), the user chose:

- **Path B** (identity sharing + admin-side re-invite) as the near-term plan.
- **MIP-06 (Path A) deferred** — and at end of session, explicitly *paused*
  rather than scheduled. User does not want MIP-06 work yet.
- **QR-only** transport for the device-link payload.
- **"Automatic where possible, request where not"** for re-invite UX.
- **Run the interop spike before per-device-slot implementation.**
- **Full §1.1–1.6 scope** for the first deliverable.

The user then asked for an analysis of MIP-06 PR #44's merge probability
(captured in §10), then asked about the "Marmot signer" alternative direction
(captured in §11), then stopped the session.

**No code was written for this plan.** This is purely a design document.

The plan file (`ai-tasks/multi-device-phase1-plan.md`) is currently **untracked**
in git — it exists in the working tree but has not been committed. Whoever picks
this up should decide whether to commit it on the current branch
(`chore/avalonia-upgrade-and-mobile-head` at session pause) or move it to a
dedicated branch.

The most recent committed work in the repo at session pause is the QR scanner
feature on `feature/qr-scan-newchat` (4 commits, pushed to
`origin/feature/qr-scan-newchat` during this session, PR not yet opened).
That feature is unrelated to multi-device but its NIP-19 decoder
(`src/Scramble.Core/Crypto/Nip19.cs`) and Android QR scanner
(`src/Scramble.Android/Activities/ScanQrActivity.cs` +
`Services/MlKitQrCodeAnalyzer.cs`) are both useful prerequisites for Phase 1
§4.4 if/when Phase 1 starts.

## 1. Framing — why "export keypackages" is the wrong mental model

The user's intuition is "if I export my keypackage to my other device, both devices
will be in the same chat." That conflates four genuinely different problems:

| Path | What it is | Status |
|---|---|---|
| **A — MIP-06** | Each device is its own MLS leaf under one Nostr identity; both join groups via External Commits, no admin needed. | **Paused.** Phase 2 / future. Blocked on upstream merge + MDK pin bump. See `mip06-multi-device-report.md`. User explicitly does not want this now. |
| **B — Identity-sharing + re-invite** | Both devices share the Nostr identity (via Amber or exported privkey); each gets its own MLS leaf via normal Welcome from a group admin. | **THIS PLAN.** Works today. Plan written but not started. |
| **C — Clone MLS state** | Copy device 1's whole MLS database to device 2 so they share one leaf. | Forbidden by MLS — ratchet allows one consumer per leaf per epoch. Will silently desync. **Not viable.** Refused. |
| **D — History sync** | New device can read messages from before it joined. | Forbidden by MLS forward secrecy at protocol level. App-layer encrypted backup is a separate future feature. Out of scope. |

This document plans **B**, designed so the work doesn't paint into a corner for **A**.

## 2. Existing groundwork already in place

Confirmed by reading the codebase before writing this plan:

- **Amber / NIP-46 is fully wired** (`ExternalSignerService.cs`, `INostrEventSigner.cs`,
  `User.IsRemoteSigner` at `src/Scramble.Core/Models/User.cs:79`,
  `LoginViewModel.GenerateNostrConnectAsync` at `src/Scramble.Presentation/ViewModels/LoginViewModel.cs:194`).
  A second device can already pair to the same Amber instance through the existing
  login flow — there's no "transfer the Nostr key" problem for Amber users.
- **Auto-publish KeyPackage on first launch** is already wired
  (`MainViewModel.cs:684` calls `MessageService.AutoPublishKeyPackageIfNeededAsync`).
- **KeyPackage fetch returns a list** and call sites already iterate
  (`ChatListViewModel.LookupKeyPackagesAsync` at line 1005,
  bot invite path at line 1155).
- **QR scanner** (Android, this week's `feature/qr-scan-newchat` branch).
- **NIP-19 / NIP-21 codec** (`Nip19.cs`) — handy for QR payload framing.

What's **missing** that this plan adds:

- No "add another device" entry point.
- No identity-export path for local-key users (no Amber).
- KeyPackage slot is per-identity, not per-device — second device's KP **overwrites**
  the first device's KP under the same `(pubkey, kind=30443, d-tag)` addressable slot.
  This is the silent-breakage problem that must be solved regardless of how identity
  sharing happens.
- No "invite my new device to existing chats" UX for groups where the existing
  device is admin.
- No forward-secrecy disclosure for joiners.

## 3. Architecture decisions

### 3.1 Per-device KeyPackage slots

**Problem.** Today `MlsService.GenerateKeyPackageAsync`
(`src/Scramble.Core/Services/ManagedMlsService.cs:265`) lazily generates a stable
`_keyPackageSlotId` and reuses it for the lifetime of the identity. This is correct
MIP-00 lifecycle hygiene **for one device**: rotating a KP overwrites the slot rather
than accumulating dangling ones.

It's **wrong for multi-device.** When device 2 publishes its KP under the same
`(pubkey, 30443, d-tag)`, it replaces device 1's KP. New invitees fetch only device 2;
device 1 silently misses Welcomes forever.

**Decision.** Slot ID derivation moves from "per-identity, persisted" to
"per-device-install, persisted." Each Scramble install generates its own slot ID on
first launch and persists it; rotation within an install still overwrites that
install's slot (preserving lifecycle hygiene). Across installs, slots differ, so
multiple KPs coexist under one identity at distinct addressable-event coordinates.

**MIP-00 compatibility.** MIP-00 mandates addressable events with a stable d-tag for
KP **lifecycle**. It does not forbid one identity from owning multiple addressable
slots — addressable events are keyed by `(pubkey, kind, d-tag)`, so two distinct
d-tags coexist correctly. This is the same direction futurepaul's MIP-06 review
comment pushes upstream ("KeyPackages should be replaceable per-client, not per-user").
We're early to it but consistent with where the spec is going.

**Risk: third-party interop.** Some other Marmot clients may still naively pick "the
first KP returned" rather than iterating. **Pre-implementation spike** is in §6.1.

### 3.2 Identity sharing — Amber path

For users logged in via Amber (`User.IsRemoteSigner == true`), the second device
literally repeats the original login flow. No new code path is needed for the
*pairing* itself — only an entry point that produces the `nostrconnect://` URI from
Settings instead of from the Login screen.

**Why this is essentially free.** The whole NIP-46 stack is already proven and the
second device's Amber session is independent of the first's — Amber maintains separate
authorizations per ephemeral client pubkey but signs everything with the user's
single Nostr key. Both devices end up with the same `User.PublicKeyHex` and
delegate signing to Amber independently.

**Decision.** Reuse `LoginViewModel.GenerateNostrConnectAsync` infrastructure. The
"Add another device" entry point for an Amber-paired user just bounces them into a
re-display of the same QR/URI mechanics, *intended for scanning by their other
device's Amber install* (not by Scramble — Amber consumes the URI).

### 3.3 Identity sharing — local-key path

For users without Amber, we have to actually transfer the Nostr private key. **This
is the security-sensitive bit.**

**Decision: passphrase-encrypted QR.** Never display a plaintext-key QR.

Payload:

```text
scramble-link:v1:<base64url(
  ChaCha20-Poly1305.encrypt(
    key   = HKDF-SHA256(salt=ASCII("scramble-link-v1"),
                        ikm=Argon2id(passphrase, salt=12-byte-random, t=3, m=64MB)),
    nonce = 12-byte-random (prepended to ciphertext as standard),
    aad   = ASCII("scramble-link-v1"),
    plaintext = TLS-serialize(LinkBundle)
  )
)>

struct LinkBundle {
    uint8  version;                       // 1
    opaque nostr_privkey[32];
    opaque relay_urls<1..2^16-1>;         // length-prefixed list of UTF-8 strings
    opaque configured_bots<0..2^16-1>;    // optional; same layout as relays
}
```

Argon2id parameters target ~500ms on a phone — slows brute force enough that a
4-word passphrase (Diceware) is comfortably safe.

**Workflow.**

1. Existing device: prompt for passphrase (validate ≥ 4 Diceware words or ≥ 12 chars).
2. Build payload above. Show QR (always fits — payload is < 250 bytes uncompressed).
3. New device: scan QR; prompt for the same passphrase; decrypt; persist; log in.
4. After successful import, existing device shows a one-time reminder: "Your private
   key now exists on 2 devices. Treat both with the same care."

**Why not skip the passphrase?** A QR with a plaintext nsec is leaked the first time
anyone shoulder-surfs the screen, photographs it, or logs the screencast. The
passphrase requirement is what makes the QR safe to display in normal lighting.

**Why not Diffie–Hellman pairing instead of a passphrase?** The DH pairing approach
(MIP-06's Phase 1 bootstrap) requires a bidirectional channel for the second leg.
A pure QR-only flow is one-way; the passphrase is what authenticates the second
device. When MIP-06 lands we *will* use the DH approach for the pairing payload —
but that payload carries MLS material, not the Nostr key, so the security model is
different.

### 3.4 Re-invite into existing groups

**For groups the existing device is admin in:** automatic.

After identity onboarding completes on the new device:
1. New device publishes its own KP under its per-device slot (already happens via
   `AutoPublishKeyPackageIfNeededAsync`).
2. Existing device sees a notification "Your new device is online — add it to N
   groups?" listing groups where `IsAdmin == true`.
3. On approval, walks those groups and issues normal `AddMemberAsync` calls
   targeting the new device's KP. Standard Welcome flow takes over.

**For groups the existing device is NOT admin in:** explicit handoff to a human.

Notification on the existing device: "Ask an admin of *Group X* to invite your new
device. Tap to copy your npub." Surface this for non-admin groups so the user knows
what's blocked. There is no protocol-level fix for this in Phase 1 — it's the
literal UX hole MIP-06 closes via External Commits.

### 3.5 Forward-secrecy disclosure

First launch of a paired second device shows a one-shot dismissable banner:

> Your new device can read messages from now on, but **not** messages sent before
> you added it. This is a security feature.

Banner text comes from MIP-06 §11 SHOULD-level recommendations applied early.

### 3.6 What we explicitly DO NOT do in Phase 1

- No MLS state import. Adding code that even *looks* like it's exporting MLS state
  would invite path-C catastrophes.
- No "device list" / device management UI. Phase 1 just lets you have N devices;
  managing them (revoking, naming) is a Phase 2 concern when MIP-06 brings a
  per-device leaf identity to attach a name to.
- No history sync.
- No BLE / LAN / relay-rendezvous. QR only.
- No own-message echo dedup. (For Amber users, both devices might echo "I sent
  message X" because both devices independently decrypt the group event. This needs
  client-side dedup later, but it's a polish item, not a blocker.)

## 4. Implementation plan

### 4.1 Pre-work — interop spike (§6.1) before any code

Run the spike. If results are bad, the rest of this plan needs revision.

### 4.2 Per-device KeyPackage slot (Backend)

- `src/Scramble.Core/Services/ManagedMlsService.cs:56` — change `_keyPackageSlotId`
  comment + initialization to per-device. Persisted state file already stores it; no
  migration needed because each install already has its own state file.
- Add a unit test: two `ManagedMlsService` instances initialized with separate state
  paths produce distinct slot IDs.
- Verify call sites that fetch KPs already iterate (they do — see §2). Add an
  integration test that publishes 2 KPs under one identity to the docker test relay
  and verifies a third instance fetches both.

### 4.3 Identity onboarding entry point (UI — both targets)

Settings dialog gains an "Add another device" button.

**Branching by user type:**

```
if (CurrentUser.IsRemoteSigner) {
    // Amber path — show nostrconnect:// QR + URI for the *other* device's Amber to scan
    // Reuse existing LoginViewModel.GenerateNostrConnectAsync visualization
    ShowAddDeviceAmberDialog();
} else {
    // Local-key path — passphrase-protected privkey export
    ShowAddDeviceLocalKeyDialog();
}
```

**Files touched (estimated):**

- `src/Scramble.Presentation/ViewModels/AddDeviceViewModel.cs` (new) — orchestrates
  passphrase prompt, payload construction, QR-bytes generation. For Amber path,
  thin wrapper around `LoginViewModel`'s existing helper extracted to a shared service.
- `src/Scramble.Core/Services/IdentityLinkPayload.cs` (new) — pure encode/decode for
  `scramble-link:v1:` URIs. Vector-tested.
- `src/Scramble.Core/Crypto/PassphraseKdf.cs` (new) — Argon2id wrapper. We may
  already have an Argon2 dep via libsodium; needs verification.
- `src/Scramble.Android/Fragments/AddDeviceFragment.cs` + layout (new).
- `src/Scramble.Android/Fragments/SettingsFragment.cs` — entry point button.
- `src/Scramble.UI/Views/AddDeviceView.axaml` (new).
- `src/Scramble.UI/Views/SettingsView.axaml` — entry point button.

### 4.4 New device's "scan device-link QR" flow

- Android scanner is the existing `ScanQrActivity` from this week. Extend
  `MlKitQrCodeAnalyzer` to recognize `scramble-link:v1:` prefix in addition to
  npub/nprofile/nostr:; route to a different result handler. Probably cleaner to
  decide in the *receiving Activity* (NewChatFragment vs. SettingsFragment) what
  prefixes it accepts; the analyzer stays format-agnostic.
- Desktop: paste-as-text fallback. Accepted as a CLAUDE.md exception (same rationale
  as the QR scanner — desktops don't have rear cameras pointing at QRs in normal
  use). The "Add this device to my account" view on desktop has a textbox + paste
  button. Document in the plan.
- After scan/paste: prompt for passphrase, decrypt, validate, persist, log in.
  Existing `LoginViewModel.LoginWithPrivateKey`-style code path applies.

### 4.5 "Invite my new device to existing chats"

- After new-device login completes, it publishes its KP normally.
- New device emits a `kind: 24XXX` ephemeral self-tagged event "I'm a new device for
  npub X" — but actually, no, that's its own protocol and we don't need it. Simpler:
- Existing device polls KPs for own pubkey periodically (already does via
  `AutoPublishKeyPackageIfNeededAsync` precondition check at `MainViewModel.cs:678`).
  When it sees a KP it didn't publish (different slot ID **or** different KP HPKE
  pubkey), it knows another device is online.
- Show notification + "Add to N groups" panel. Walk admin groups, call existing
  `AddMemberAsync` per group with the foreign KP.
- For non-admin groups: list them with "Ask the admin to add your new device" +
  copy-npub button.

**Files touched (estimated):**

- `src/Scramble.Presentation/ViewModels/MainViewModel.cs` — extend KP-check logic to
  detect peer KPs, surface as `Reactive` property.
- `src/Scramble.Presentation/ViewModels/InviteMyDevicesViewModel.cs` (new).
- `src/Scramble.Android/Fragments/InviteMyDevicesFragment.cs` + layout (new).
- `src/Scramble.UI/Views/InviteMyDevicesView.axaml` (new).

### 4.6 Forward-secrecy disclosure

- One-shot banner. Use existing notification/banner infrastructure if any; otherwise
  a simple `Reactive` flag persisted in user settings.
- Trigger: detect "this is the first time this MLS DB has been opened by an identity
  that already has a published KP from another slot" — i.e., we just got linked.

### 4.7 Tests

- **Unit:**
  - `IdentityLinkPayload` round-trip for known passphrase + known privkey + known
    relays. Vector test.
  - `PassphraseKdf` matches a known Argon2id vector.
  - Per-device slot IDs are distinct across DB instances.
  - Wrong-passphrase decrypt fails cleanly with a typed error (not silent garbage).
- **Integration (Scramble.Core, headless):**
  - Two `ManagedMlsService` instances under one identity → both publish KPs → third
    instance fetches → returns both. Lives next to `HeadlessMessagingExtendedTests`.
  - Admin device adds two device-leaves of one foreign identity to one group → both
    decrypt the next group message independently.
- **Manual smoke:**
  - Amber path on laptop + phone against `tests/local-relay`.
  - Local-key path with passphrase on laptop + phone.
  - "Invite my new device" panel happy path + non-admin-group warning path.

## 5. UI inventory (CLAUDE.md compliance)

| Surface | Android | Desktop | Notes |
|---|---|---|---|
| "Add another device" Settings entry | New menu item | New panel button | Both targets, symmetric |
| QR display (Amber path) | Reuse `MyNpubQrPngBytes` view | Reuse `MyNpubQr` Avalonia view | Existing render pipeline |
| QR display (local-key path, passphrase-encrypted) | Same view | Same view | Different payload, same widget |
| Passphrase prompt | `MaterialAlertDialog` + `TextInputLayout` (password mode) | `TextBox` with `PasswordChar='*'` | Standard widgets |
| QR scan (new device) | `ScanQrActivity` (already shipped) | **Paste textbox** — CLAUDE.md exception, justified inline | Same exception precedent as QR scanner |
| Decrypt-passphrase prompt (new device) | Same as above | Same as above | Symmetric |
| "Invite my new device" panel | New fragment | New view | Symmetric |
| Forward-secrecy banner | Snackbar (one-shot, persist-dismissed flag) | InfoBar / Border (same persistence) | Symmetric copy |
| "Your privkey is on 2 devices now" reminder (existing device, after pairing) | Material dialog | Avalonia dialog | Symmetric |

The two desktop-exception cells (camera scan) are the only deviations from
"both targets in sync." Same justification as the QR scanner exception in
`ai-tasks/qr-scanner-feature-analysis.md`.

## 6. Open risks and how we'll de-risk them

### 6.1 KeyPackage interop spike (BLOCKING)

**Run before writing any per-device-slot code.** Estimated 1–2 hours.

#### Assumption being tested

§3.1 says "give each device its own KeyPackage slot (different `d-tag` on the
kind-30443 addressable event), so both devices' KPs coexist under one identity,
and inviters fetch both and Welcome both."

This is coherent **if** other Marmot clients (Whitenoise, 0xchat, etc.) iterate
over all returned KeyPackages when inviting someone. Scramble's own code does
(verified — `ChatListViewModel.cs:1005` and `:1155` both loop over the returned
list). We do **not** know what other clients do.

#### Possible behaviours and their consequences

When you fetch kind-30443 events for one pubkey from a relay, you can get back
multiple events if that pubkey has multiple distinct `d-tag` slots. A client
receiving that list can:

| # | Behaviour | Outcome | Plan impact |
|---|---|---|---|
| 1 | Iterate all, send N Welcomes | Both devices joined | ✅ Per-device slots work. Ship as planned. |
| 2 | Pick newest by `created_at` | Only newest device joins; older silently misses | ❌ Per-device slots break silently. Fallback needed. |
| 3 | Pick first by relay arrival order | Non-deterministic; some invites land on A, some on B | ❌ Worst case. Per-device slots unusable. |
| 4 | Pick by heuristic (lowest event id, lex d-tag, ...) | Deterministic but only one device wins | ❌ Same as #2 functionally. |
| 5 | Reject multi-KP identities | Invite fails entirely | Very unlikely (would violate MIP-00 explicitly); if so, plan needs major rework. |

The failure modes (2–4) fail **silently** — the user notices "my laptop never
gets invites" weeks later with no idea why. This is exactly why the spike is
worth running upfront: the cost is 1–2 hours, the cost of debugging it
retroactively after shipping is much higher.

#### Concrete steps

1. **Set up two Scramble dev installs** under one Nostr identity. Either: run
   two separate Scramble.Android emulators with synced identities, OR hack
   `ManagedMlsService.cs:265` locally to generate a fresh `_keyPackageSlotId`
   on each call so one install produces two slot IDs. Point both at the local
   docker test relay (`tests/local-relay`).
2. **Publish 2 KeyPackages** for that identity, one from each "device," at two
   distinct d-tags. Verify with `nak req -k 30443 -a <pubkey>` against the
   relay — should return 2 events.
3. **Install a fresh Whitenoise build** (Android APK or desktop). Log in as a
   different identity. Try to invite the multi-KP test identity to a new group.
4. **Observe**: did Whitenoise issue 1 Welcome or 2? If 1, which one — newest,
   first-arrived, lowest event id? Capture by watching relay traffic.
5. **Repeat with publish order reversed** to distinguish "newest wins" from
   "first-arrived wins" from "iterates all."
6. **Optional**: repeat with 0xchat if it implements Marmot/MLS chat (needs
   verification — 0xchat may only do NIP-17 DMs, not MLS groups).

#### Fallback strategies if behaviour is 2–4

- **Periodic KP republish** so each device "wins" the freshness race fairly
  often, catching a fraction of invites. Crude but functional.
- **Coordinated rotation** via a tag or convention — needs upstream agreement,
  gets complicated fast.
- **Defer multi-device KP to MIP-06** entirely — Phase 1 ships only
  Amber-pair + identity-sharing, document that "second device only gets new
  chats explicitly invited to it." Less ambitious but ships sooner.

#### Why this is BLOCKING

The per-device-slot code change in `ManagedMlsService.cs` is a 5-minute edit.
The **risk** isn't that it's hard to write — it's that the failure mode of an
incorrect assumption is hard to detect and hard to debug retroactively.
Upfront verification answers the question definitively before any commit.

### 6.2 Argon2id dependency

We may not have it. Need to verify. Alternatives in order of preference: libsodium
binding (probably already there for NIP-44), `Konscious.Security.Cryptography`, or
fallback to PBKDF2-HMAC-SHA512 with high iteration count. Argon2id is preferred for
GPU/ASIC resistance.

### 6.3 Amber session limits

Amber may rate-limit or have UX friction with a single user authorizing multiple
client pubkeys. Worth a 15-minute test on real Amber before assuming "just call
GenerateNostrConnectAsync from a new entry point" works seamlessly.

`ai-tasks/nip46-rate-limit-timeout.md` already exists — relevant prior art to read
before starting §4.3.

### 6.4 `IsAdmin` detection accuracy

`MainViewModel.Chats` per-chat `IsAdmin` flag reliability needs a pre-flight check
before we walk admin groups in §4.5. If it's stale or misses cases, the auto-invite
flow either spams non-admin groups (visible failure — reject) or misses admin groups
(silent failure — bad).

### 6.5 Phase 1 → Phase 2 forward compatibility

This is the design constraint that shaped the plan. Concrete points:

- Per-device slots (§3.1) is exactly what MIP-06 needs and what futurepaul wants
  for upstream MIP-00 anyway — no rework.
- The Add-Device entry point branches by user type today; in Phase 2 it gains a
  third branch for "use MIP-06 External Commit if all your groups support it." The
  existing two branches don't change.
- "Invite my new device" panel becomes a fallback for non-MIP-06 groups in Phase 2.
  Same UI, narrower applicability. No code thrown away.
- Forward-secrecy banner copy is unchanged.
- Local-key passphrase QR is replaced for MIP-06 by the DH-pairing payload, but
  only conceptually — they coexist (one transfers Nostr key, the other transfers
  MLS material). Both can ship.

## 7. Estimated scope

≈ 2 weeks of focused work, broken roughly:

| Block | Days |
|---|---|
| Interop spike (§6.1) + per-device slot change (§4.2) + tests | 2 |
| Identity-link payload + Argon2id wiring + vector tests (§3.3, §4.3 backend) | 2 |
| Add-Device entry point UI on both targets (§4.3, §4.4) | 3 |
| Invite-my-new-device flow on both targets (§4.5) | 3 |
| Forward-secrecy banner + post-pair reminder + polish (§4.6) | 1 |
| Integration tests + manual smoke + buffer | 2 |

## 8. Out of scope, explicitly

- MLS state cloning (path C). Will not implement; will refuse if asked.
- History sync (path D). Future feature.
- MIP-06 External Commits (path A). `mip06-multi-device-report.md` covers it.
- Device naming / management UI (Phase 2 with MIP-06's `0xF2EF` extension).
- BLE, LAN, relay-rendezvous transports.
- Own-message echo dedup across devices (polish item, not blocker).
- A "this device was just added on" event surfaced in the chat timeline (Phase 2;
  related to MIP-06 device-added events).

## 9. Decision log

- **B + design-toward-A** chosen over standalone A or D, on user direction. A is
  the right answer but blocked; B is the working stopgap that doesn't conflict with A.
- **QR-only transport** chosen on user direction. Other transports deferred.
- **"Automatic where possible, request where not"** chosen for re-invite UX, on
  user direction. Implemented in §4.5.
- **Interop spike before per-device-slot implementation** chosen on user direction.
  See §6.1.
- **Full §1.1–1.6 scope** chosen on user direction. Plan reflects that.
- **Plan in `ai-tasks/multi-device-phase1-plan.md`** chosen on user direction.

## 10. Appendix — MIP-06 merge probability assessment

> **Date of assessment**: May 11, 2026. Re-check before §4.2 implementation if
> more than ~6 weeks have passed.

### Summary

**Estimate: ~75–85% chance MIP-06 merges within 3–6 months from now.** The Phase 1
plan above is robust whether MIP-06 lands or not.

### Hard data from PR #44 (snapshot)

- **State**: open, marked ready-for-review on Mar 27 2026 (was draft before).
- **Author**: erskingardner — Marmot project lead, Member-level association.
- **Approvals**: 1 (`dannym-arx`, Apr 7).
- **Pending review**: `hzrd149` (requested Apr 7), `jgmontoya` (engaged but no
  formal approval).
- **Mergeable state**: `clean` — no conflicts with master.
- **Open**: ~12 weeks (created Feb 19 2026).
- **Last code activity**: Apr 7 2026. Last comment activity: Apr 13 2026.
  ~4 weeks of silence at time of assessment.
- **Repo overall activity**: master last pushed May 8 2026 — project is alive.
- **Closes**: issue [#43](https://github.com/marmot-protocol/marmot/issues/43)
  (the tracked feature ask).

### Signals favoring merge (+)

1. **Author is the project lead.** Not an outside contributor whose PR can rot.
2. **No unresolved technical blockers.**
   - Early HKDF-hash-pinning blocker (alltheseas) → fixed.
   - Per-user device cap question (jgmontoya) → resolved as "explicitly no
     protocol-level cap in v1; can be added by bumping `MarmotMultiDevice.version`."
   - "Newest KP wins" UX concern (futurepaul) → punted to a separate future MIP-00
     amendment for replaceable KPs. Not a blocker for *this* PR.
3. **One core-reviewer approval already in.** Marmot is small; one approval is
   often enough for a spec PR.
4. **Reviewers are engaged**, not silent. Iteration toward consensus, not stuck.
5. **No fundamental design objections.** Everything raised is implementation
   detail or scope, never "this approach is wrong."
6. **Spec PR, not code.** No CI threshold to satisfy, no breaking-change blast
   radius. Bar for merge is "core reviewers agree this is the spec we want."

### Signals against merge or that delay it (−)

1. **4-week silence** (Apr 13 → May 11). Could be normal review timing or
   stalled. Concerning if it extends past mid-June.
2. **vitorpamplona's "Nostr culture: users test many clients"** objection got an
   `🤷‍♂️ I'm not really sure that that is something we can completely solve for`
   from erskingardner. Acknowledged-but-punted. Could resurface as a hard block
   from vitorpamplona, but more likely it'll be accepted as out of scope for the
   MIP itself.
3. **vitorpamplona is influential** and hasn't approved. He hinted at preferring a
   "Marmot signer" architectural direction (see §11 below). If he formalizes that
   preference into a request-for-changes, MIP-06 could be delayed several months.
4. **Replaceable-KP coupling.** If erskingardner decides "we need the MIP-00
   replaceable-KP amendment to land *first* because MIP-06 multiplies the
   newest-KP-wins problem," that's a 1–3 month detour through MIP-00 changes
   before MIP-06 can merge. Probability: ~15%.

### Scenarios that could prevent merge entirely (~15–25% combined)

| Scenario | Probability | Effect on Phase 1 plan |
|---|---|---|
| Architectural pivot to "Marmot signer" model | ~10% | Phase 1 still ships and is still valuable. Phase 2 design changes substantially. |
| Replaceable-KP MIP-00 amendment must land first | ~15% | Adds 2–4 months to Phase 2 timeline. Phase 1 unaffected; per-device-slot decision stays valid. |
| Project loses momentum entirely | <5% | Phase 1 still ships; Phase 2 indefinite. |

### Trigger to revise this assessment

- **Mid-June 2026**: if no further activity, drop estimate to ~50%.
- **`hzrd149` substantive review**: clarifies path either way.
- **`vitorpamplona` formal request-for-changes**: drop to ~40% for 2026 merge.
- **Any KeyPackage replaceability decision posted**: re-evaluate Phase 1 §3.1
  before §4.2 implementation. This is the only Phase 1 design point exposed to
  upstream conventions.

### Implications for Phase 1

- **Don't gate Phase 1 on MIP-06.** Even if MIP-06 merges next month, Scramble
  can't ship MIP-06 implementation immediately — the MDK pin bump
  (`csharp-mls-fixes-plan.md`) is weeks of work, and ecosystem interop
  (Whitenoise / 0xchat catching up) adds more. Phase 1 fills the gap during the
  entire MIP-06 lead time.
- **Watch one specific upstream decision**: any move on KeyPackage
  replaceability convention. If upstream picks a specific d-tag scheme for
  per-client KPs, align §3.1 with it. 5-line code change, no design rework.
- **Otherwise: every Phase 1 decision survives MIP-06 verbatim** (per-device KP
  slots, identity sharing UX, forward-secrecy banner) **or becomes a fallback
  path** (admin-side re-invite, still needed for pre-MIP-06 groups forever).

## 11. Appendix — The "Marmot signer" alternative direction

Vitorpamplona raised this in PR #44 review and erskingardner has acknowledged it
elsewhere. It's not currently a competing PR, just an architectural conversation
that could redirect MIP-06's trajectory. Worth understanding because it's the
most likely cause of a Phase 2 design pivot.

### What a "Marmot signer" would be

A NIP-46-style remote signer dedicated to Marmot/MLS, holding **all** of a user's
MLS state (signing keys, encryption keys, ratchet trees, secret trees, stored
KeyPackages) on one trusted device. Clients on phone/laptop/web become thin UI
shells that delegate every MLS operation to the signer over an authenticated
channel — much like Amber holds the Nostr key today and signs events on behalf of
clients.

### Why proponents like it

- **One MLS leaf per user, period.** No multi-leaf coalescing in chat UI, no
  identity-coalescing logic, no "device added" surfacing — the group sees one
  member, the user sees their messages on whichever client they happen to open.
- **History sync becomes trivial.** Any new client just asks the signer for
  decrypted messages. The signer can reveal arbitrary history because it holds
  the full ratchet state.
- **Tested-client risk shrinks.** Vitorpamplona's concrete worry: "users test 5
  Nostr clients per week with their production nsec." If a tested client never
  receives the MLS signing key — only signed-event responses from a vetted signer
  — a buggy/malicious client can't fork the group, leak ratchet state, or
  permanently divide an epoch. The signer becomes the trust anchor and clients
  are constrained.
- **Existing NIP-46 / Amber UX precedent.** Users already understand "open Amber
  to approve." Marmot signer extends the same mental model.

### Why MIP-06 went the other direction anyway

- **Single point of compromise**. The signer holds *all* MLS state for a user. If
  it's stolen, every group that user is in is fully readable forward and
  backward. MIP-06's per-device leaf model means compromising one device only
  exposes that device's messages from its join epoch onward.
- **Single point of failure**. Signer offline = all messaging offline. No
  graceful degradation. MIP-06's independent leaves keep working when other
  devices are offline.
- **Latency on hot paths.** Every send/receive becomes an RTT to the signer.
  Group decryption is in the read-message hot path; chat UIs would feel
  noticeably slower than the current "keys in process" architecture. NIP-46 for
  events tolerates this; per-message MLS for an active chat is a different
  workload.
- **Concentrated attack surface for relays/networks.** If the signer is reachable
  via Nostr relays (the only practical transport today), the signer's traffic
  pattern becomes a strong fingerprint of "user is in MLS group X, currently
  active." MIP-06's distributed leaves spread that signal.
- **Doesn't compose with existing Amber.** A user already pairs Amber for Nostr
  signing. Adding a *second* always-on signer for MLS doubles the "your phone is
  the bottleneck for everything" UX problem.
- **MLS-Architecture-as-such concern.** RFC 9420 explicitly assumes per-device
  signing/encryption keys. Putting the keys behind a remote signer is operating
  MLS in a mode it wasn't designed for — not impossible, but you lose some of
  MLS's deniability and transcript properties because the signer becomes a third
  party in every group's transcript chain.

### How they compare on each axis

| Axis | MIP-06 (per-device leaves) | Marmot signer (centralized state) |
|---|---|---|
| Number of MLS leaves per user | N (one per device) | 1 |
| History on a new device | Forward-only (FS preserved) | Full history available |
| Single device compromise blast radius | Device's join-epoch onward, that device only | All groups, full history |
| Single device offline | Other devices keep working | Signer offline → all clients offline |
| Send/receive latency | Native | +1 RTT per operation |
| Buggy 3rd-party client risk | Client can't read groups it wasn't invited to; can break its own leaf | Client constrained to signer's API; bugs much narrower |
| Composes with Amber for Nostr signing | Independent — orthogonal | Adds a second always-on signer |
| Spec status | PR open, ~75–85% likely to merge | No spec, no PR, conversational only |
| Signaling/group consensus | Required (`0xF2F0` capability gate) | Not required (group sees one leaf) |
| Existing MLS stack support | Standard External Commit + External PSK | Requires building a new signer protocol |
| Scramble code already aligned | Mostly (per-device DB exists; per-device slot is the gap) | No (would require centralizing state away from clients) |

### Hybrid and middle-ground possibilities

These aren't mutually exclusive, and reality probably lands somewhere between:

1. **MIP-06 + optional Marmot-signer mode.** Most users run MIP-06 with their
   own per-device leaves; security-conscious users (or "I have 5 test clients")
   users opt into a signer that handles all their MLS for them. Both modes
   coexist on the same protocol because, at the wire level, an MLS leaf is an
   MLS leaf — the signer model just collapses N leaves into 1 leaf operated by
   N clients.
2. **MIP-06 today, signer-mode-on-top later.** Ship MIP-06 first because it's
   the more conservative architectural extension. Layer a signer mode on top
   later as an *application-layer* feature that doesn't require new protocol bits
   — clients just delegate operations to a designated trusted device they own.
3. **Signer for "public" identity, per-device for "private" identity.** Vitor's
   risk is specifically about test-clients touching production groups. Maybe the
   right answer isn't a new architecture but a recommendation: "use one identity
   for testing clients, a separate identity for groups you actually care about."
   Cultural fix, not protocol fix.

### What the Scramble code already supports

Scramble's existing Amber/NIP-46 integration **is** the closest thing to a signer
model that exists today, but only for Nostr-event signing — not for MLS state.
For Amber-paired users:

- Nostr signing (events) → Amber. Already done.
- MLS signing/encryption keys → still local on each device. Not signer-delegated.
- MLS ratchet state → still local on each device. Not signer-delegated.

A future "Marmot signer mode" in Scramble would extend Amber-style delegation to
MLS operations. Phase 1 doesn't preclude this — both directions can coexist.

### My take

The signer model is intellectually appealing but **MIP-06 is the right thing to
ship first** because:

1. It's the more conservative extension of MLS's design intent.
2. It fails gracefully (one device offline ≠ no messaging).
3. It composes cleanly with Amber for Nostr signing without requiring a second
   always-on dependency.
4. The signer model can be added later as an opt-in mode without breaking
   anything; the reverse is harder.

The "tested-clients-risk" concern Vitor raises is real, but it's not unique to
multi-device — it exists today every time a user logs into a new Nostr client
with their nsec. The right fix is probably cultural ("don't paste your
production nsec into random clients") plus better signer-only UX (Amber making
it easier to *not* expose the key), not a protocol rearchitecture.

**Implication for Phase 1**: the signer alternative doesn't change anything in
the plan above. If Marmot the project pivots to signer-first (low probability,
~10%), Phase 1 still ships and remains useful as a stopgap during the (longer)
build-out of a signer protocol.

## 12. References

- `ai-tasks/mip06-multi-device-report.md` — Phase 2 / future protocol-level fix.
- `ai-tasks/completed/link-device-relay-selection.md` — earlier work on linking
  device flows; complementary, not overlapping.
- `ai-tasks/completed/amber-phase1-nip44-giftwrap.md`,
  `amber-phase2-mls-event-signing.md`,
  `amber-phase3-cleanup-tests.md` — Amber integration history.
- `ai-tasks/nip46-rate-limit-timeout.md` — Amber timing concerns relevant to §6.3.
- `ai-tasks/completed/mip00-keypackage-compliance.md` — current MIP-00 KP slot
  behavior, relevant to §3.1.
- `ai-tasks/qr-scanner-feature-analysis.md` — CLAUDE.md desktop-camera exception
  precedent, relevant to §5.
- `src/Scramble.Core/Services/ManagedMlsService.cs:56,265` — current per-identity
  slot behavior to be changed.
- `src/Scramble.Presentation/ViewModels/LoginViewModel.cs:194` — Amber NostrConnect
  flow to be reused.
- `src/Scramble.Presentation/ViewModels/MainViewModel.cs:678,684` — KP-presence
  check + auto-publish to be extended for peer-KP detection.
- `src/Scramble.Presentation/ViewModels/ChatListViewModel.cs:1005,1155` — call
  sites that already iterate fetched KP lists.
