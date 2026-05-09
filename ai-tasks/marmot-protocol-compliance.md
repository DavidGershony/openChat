# Marmot Protocol Compliance — Plan of Work

## Goal

Bring `Scramble.Core` into compliance with the Marmot protocol's CRITICAL
correctness requirements around commit/welcome publishing, race resolution,
and post-join hygiene. Drive the work from the spec, not from intuition.

The catalyst is the publish-failure bug surfaced by
`tests/Scramble.Diagnostics/RelayHarness/PublishFailureTests.cs`:
`MessageService.AddMemberAsync` advances local MLS state before publishing,
ignores the publish OK result, and unconditionally publishes the Welcome.
Two `FaultyRelay` tests prove this deterministically. The fix touches
multiple layers and overlaps with several other compliance gaps, so we
plan it as one batch of work rather than a one-off patch.

## Source documents

Marmot spec, cloned to `C:\Users\david\claudeWork\marmot`:

- `00.md` — KeyPackages
- `01.md` — Group setup and the Marmot Group Data Extension (`0xF2EE`)
- `02.md` — Welcome Events (kind 444 / wrapped 1059)
- `03.md` — Group Messages (kind 445): commits, application messages
- `04.md` — Media (skim)
- `05.md` — (skim)
- `threat_model.md` — explicit cross-references between MUSTs and threats

Plus RFC 9420 (MLS) for primitives.

## Normative requirements (precise citations)

Each row is a MUST or SHOULD that drives at least one piece of code.

### MIP-03 §"Commit Message Race Conditions" — sending

> Clients sending Commits MUST:
> 1. Send the Group Event
> 2. Wait for acknowledgment — MUST NOT apply the Commit locally until at least one relay confirms receipt
> 3. Then apply locally
> 4. Then send any associated Welcomes

### MIP-03 §"Commit Message Race Conditions" — receiving

> When receiving multiple Commits for the same epoch, clients MUST apply exactly one using:
> 1. Earliest `created_at`
> 2. Lex-smallest `id` as tiebreaker
> 3. Discard others
>
> Clients SHOULD retain previous group states temporarily to enable recovery from forked states.

### MIP-03 §"Exception - Initial Group Creation"

> The very first Commit that creates a group MUST NOT be sent to relays.
> Initial Commit establishes epoch 0 and exists only locally.

### MIP-02 §"Timing Requirements"

> Clients creating Welcome Events MUST:
> 1. Wait for confirmation — Don't send the Welcome until relays confirm receipt of the Commit
> 2. Process before welcoming
>
> CRITICAL: applies to ALL member additions after initial group creation.
> Initial epoch-0 group creation is the only exception.

### MIP-02 §"Processing Requirements" — receiving Welcome

MUST:
1. Verify KeyPackage match
2. Process MLS Welcome
3. Validate group state (incl. Marmot Group Data Extension `0xF2EE`)
4. Store group information
5. Rotate KeyPackage — publish new `kind:30443` under same `d` tag if consumed was kind:30443
6. Securely delete `init_key`
7. Catch up on outstanding Commits, then self-update

### MIP-02 §"Self-Update Timing"

> MUST: Perform self-update within 24 hours of joining the group, even if catch-up is partial.
>
> RECOMMENDED order: process outstanding Commits first, then self-update.
>
> RECOMMENDED target: complete self-update before sending application messages.

### MIP-03 §"Encryption Details"

> ChaCha20-Poly1305 with `encryption_key = MLS-Exporter("marmot", "group-event", 32)`
> AAD = empty byte string (for both encrypt and decrypt)
> Random 12-byte nonce per event, never reuse
> If duplicate outbound nonce detected: MUST NOT transmit, SHOULD self-update Commit immediately
> If RNG cannot produce 12-byte nonce: MUST abort, MUST NOT use deterministic fallback

### MIP-03 §"Application Messages"

> Inner events MUST use the sender's Nostr identity in `pubkey`
> Clients MUST verify the MLS sender matches the inner event's pubkey
> Inner events MUST remain unsigned (no `sig`)
> MUST NOT include `h` tags or other group identifiers

### MIP-03 §SelfRemove rules

Multiple MUSTs around admin handling (admins MUST self-demote before sending
SelfRemove, etc.). Audit will determine current Scramble coverage.

### MIP-02 §"Error Handling"

> If Welcome processing fails, clients SHOULD:
> 1. Retain KeyPackage (do NOT delete from relays)
> 2. Display clear errors
> 3. Log technical details

## Where Scramble currently violates each requirement

Cross-referenced against current code on master `fc6b3c5`. Updated by Phase 0
audit. Rows in approximate spec-order: MIP-00, MIP-01, MIP-02, MIP-03, MIP-04.

### MIP-00 — Credentials & KeyPackages

| MIP citation | Scramble file/line | Status |
|---|---|---|
| MIP-00 §"Identity Requirements": MUST use BasicCredential, identity = raw 32 bytes of Nostr pubkey | `ManagedMlsService.cs:35,90,237` (`_identity = Convert.FromHexString(publicKeyHex)`; passed to `MlsGroup.CreateKeyPackage`) | **OK** — raw 32-byte pubkey passed as identity. |
| MIP-00 §"Identity Requirements": Validate proposals — reject identity-changing Proposal/Commit | nowhere | **Unknown** — no explicit reject; relying on lower MLS layer. Worth a Phase 0.5 follow-up read of `MlsGroup.ProcessCommitCore` for identity-change rejection. |
| MIP-00 §"Required Fields" — `d` tag: random 32-byte hex, MUST NOT be empty, strictly increasing `created_at` on rotation | `marmot-cs/.../Mip00/KeyPackageEventBuilder.cs:53-57` | **OK on first publish**; **Missing on rotation** — no rotation flow exists, so the "strictly increasing created_at" rule has no implementation path. |
| MIP-00 §"Required Fields" — `encoding` MUST be `["encoding", "base64"]`; reject hex | `KeyPackageEventBuilder.cs:82` (publishes `["encoding", "base64"]`) — receiving side: not audited | **OK on publish**; **Unknown on receive** — verify inbound rejection of non-base64. |
| MIP-00 §"Required Fields" — `mls_extensions` MUST include `0xf2ee` (marmot_group_data) and `0x000a` (last_resort) | `ManagedMlsService.cs:239,253` (passes `{0x000A, 0xF2EE}` to `BuildKeyPackageEvent`) → `KeyPackageEventBuilder.cs:71-76` | **OK**. |
| MIP-00 §"Required Fields" — `mls_proposals` MUST include `0x000a` (self_remove) | `KeyPackageEventBuilder.cs:86` (`["mls_proposals", "0x000a"]`, hardcoded) | **OK**. |
| MIP-00 §"Required Fields" — `relays` MUST contain at least one valid WS URL | `ManagedMlsService.cs:252` passes `Array.Empty<string>()` → `NostrService.cs:1147-1165` backfills with connected relay URLs before signing | **OK** — backfill fires before publish; signed event includes valid relays. |
| MIP-00 §"Required Fields" — `i` tag = hex KeyPackageRef using ciphersuite hash | `KeyPackageEventBuilder.cs:60-63` (`KeyPackageRef.Compute(cs, keyPackageBytes)`) | **OK**. |
| MIP-00 §"Selecting a KeyPackage for Invitation" — selection policy: reject invalid, prefer non-last_resort, prefer highest `created_at`, lex-smallest id tiebreaker | `MessageService.cs:683` (`keyPackages.FirstOrDefault(kp => kp.IsCipherSuiteSupported)`) and `NostrService.FetchKeyPackagesAsync` (not audited) | **Likely Violated** — selection picks first cipher-supported KP without prefer-non-last-resort or created_at sort. Needs deeper read of `FetchKeyPackagesAsync` to confirm. |
| MIP-00 §"Validation" — when `i` tag present, validate it matches computed KeyPackageRef | inbound KP fetch path (not located) | **Unknown** — not seen during audit. |
| MIP-00 §"Backward Compatibility" — accept `kind:443` and `kind:30443` during migration window; prefer 30443 | `csharp-mls-fixes-plan.md` notes legacy 443 acceptance window extended to 2026-05-31; relevant fetch code not audited in Phase 0 | **Unknown** — verify in Phase 0.5. |
| MIP-00 §"Rotating KeyPackages" — SHOULD rotate after successfully joining a group (publish new under same `d` tag) | `ManagedMlsService.cs:421` only logs `"rotation recommended"` — no implementation | **Missing** — Phase 6 work. |
| MIP-00 §"When NOT to Rotate KeyPackages" — Welcome processing failure → retain KeyPackage | `ManagedMlsService.cs:441-444` (Welcome failure throws; existing KP not deleted) | **OK by accident** — KP retention is the default since rotation isn't implemented. |
| MIP-00 §"init_key Lifecycle" — single-use: delete immediately after Welcome | N/A — Scramble uses last_resort, not single-use | **N/A**. |
| MIP-00 §"init_key Lifecycle" — last_resort: retain until replacement published, then delete (with grace window MIN 24h if used) | `ManagedMlsService.cs:417-421` retains correctly but **never publishes a replacement**, so init_key is retained indefinitely | **Violated indirectly** — retention logic correct for last_resort, but the "publish replacement → delete old" loop is missing. Phase 6. |
| MIP-00 §"Secure Deletion Requirements" — zeroize memory, remove from persistent store | `ManagedMlsService.cs:194-201` (zeroes init_key + hpke private on logout/reset only) | **Partial** — zeroization is correct but only fires on `Reset()` (logout). No per-KP-rotation deletion path. |
| MIP-00 §"Signing Key Rotation" — SHOULD regularly rotate signing key in each group via Update proposal | `ManagedMlsService.cs:823-831` (`UpdateKeysAsync` calls `_mdk.SelfUpdateAsync`) — exists as API but no scheduler | **Missing scheduler** — API exists, callers don't trigger it on a cadence. |
| MIP-00 §"KeyPackage Relays List Event" (kind 10051) — SHOULD publish relay list | not audited | **Unknown**. |

### MIP-01 — Group Construction & Marmot Group Data Extension

| MIP citation | Scramble file/line | Status |
|---|---|---|
| MIP-01 §"Group Identity and Privacy" — MLS group ID never published to relays | `ManagedMlsService.CreateGroupAsync` only persists locally; only `nostrGroupId` (from 0xF2EE) used in h-tags | **OK**. |
| MIP-01 §"Compatibility Requirements" — verify ciphersuite/capabilities/extensions match across members before group creation | not audited; `MessageService.CreateGroupAsync` calls `_mlsService.CreateGroupAsync` which doesn't fetch members' KPs first | **Unknown** — likely **Missing**, but compatibility check only matters when adding members (which fetches KPs). |
| MIP-01 §"Required MLS Extensions" — group MUST include `required_capabilities`, `ratchet_tree`, `marmot_group_data` | `marmot-cs/.../Mdk.CreateGroupAsync` (lower-layer); `dotnet-mls` includes `ratchet_tree` and `marmot_group_data` by default | **Likely OK** but worth Phase 0.5 verification of which extensions are actually written into `GroupContext.extensions`. |
| MIP-01 §"Required MLS Extensions" — `required_capabilities` MUST include `self_remove` (0x000a) as required proposal type | not implemented | **Violated** — explicitly deferred as "Fix D" in `csharp-mls-fixes-plan.md`. |
| MIP-01 §"Marmot Group Data Extension" 0xF2EE — extension MUST be present in all groups | `ManagedMlsService.cs:977-981` (reads it from GroupContext); `Mdk.CreateGroupAsync` writes it | **OK** on create + read. |
| MIP-01 §"Extension Fields" — version=3 (current), validate version, reject version=0 | not audited at TLS level | **Unknown**. |
| MIP-01 §"TLS Serialization Requirements" — QUIC-style varint length prefixes for variable-length vectors | `marmot-cs/.../GroupDataExtension` (not audited line-by-line) | **Likely OK** — marmot-cs is purpose-built for this; an explicit conformance test is the right verification. |
| MIP-01 §"Image Encryption" — ChaCha20-Poly1305 with HKDF-derived key from `image_key` seed; context `"mip01-image-encryption-v2"` | `marmot-cs/.../Crypto/ImageEncryption.cs` | **Likely OK** — file exists; not read line-by-line in this audit. Phase 0.5 verify the exact context label. |
| MIP-01 §"Image Upload Identity" — HKDF derive Nostr keypair from `image_upload_key` with context `"mip01-blossom-upload-v2"` | not audited | **Unknown**. |
| MIP-01 §"Disappearing Messages" — validate `disappearing_message_secs`, reject 0 | not audited; out-of-scope per existing plan | **Missing** — declared out of scope for this batch. |
| MIP-01 §"Disappearing Messages" — auto-apply NIP-40 expiration tag on kind:445 outer event when set | not implemented | **Missing** — declared out of scope for this batch. |
| MIP-01 §"Extension Lifecycle" — admin authorization required for GCE updates | `MessageService.cs:530-533` extracts admin pubkeys but doesn't gate non-self-update commits | **Likely Missing** — admin gating relies on `MlsGroup.ProcessCommitCore` validation. Worth a Phase 0.5 read. |
| MIP-01 §"Admin SelfRemove restriction" — admin MUST self-demote via GCE before SelfRemove | SelfRemove not implemented | **N/A pending SelfRemove implementation**. |

### MIP-02 — Welcome Events

| MIP citation | Scramble file/line | Status |
|---|---|---|
| MIP-02 §"Timing Requirements" — MUST wait for relay confirmation of commit before sending Welcome (CRITICAL) | `MessageService.cs:706-735` | **Violated** — Welcome publish runs unconditionally regardless of commit OK. Already known; central bug for this batch. |
| MIP-02 §"Layered Structure" — Welcome wrapped in NIP-59 (rumor 444 → seal 13 → gift wrap 1059) | `NostrService.cs:1244-1250` (`CreateGiftWrapAsync` with kind 444 inner) | **OK**. |
| MIP-02 §"Inner Welcome Rumor Structure" — inner kind:444 rumor MUST be unsigned | inner rumor built without `sig` field; gift wrap signs the outer | **OK**. |
| MIP-02 §"Inner Welcome Rumor Structure" — `e` tag = KP event id, `relays` tag, `encoding` tag = base64 | `marmot-cs/.../Mip02/WelcomeEventBuilder.cs` (not read line-by-line) → builds tags then `NostrService.cs:1217-1218` consumes them | **Likely OK** — exists; spot-check the exact tags in Phase 0.5. |
| MIP-02 §"Processing Requirements" step 1 — Verify KeyPackage match | `ManagedMlsService.cs:400-412` (PreviewWelcomeAsync tries each stored KP) | **OK**. |
| MIP-02 §"Processing Requirements" step 2 — Process MLS Welcome | `ManagedMlsService.cs:407-412` (`_mdk.AcceptWelcomeAsync`) | **OK**. |
| MIP-02 §"Processing Requirements" step 3 — Validate group state incl. 0xF2EE | `ManagedMlsService.cs:977-981` (reads 0xF2EE from GroupContext) — but only on demand, not at Welcome time | **Likely Missing** — extension validation as part of accept flow not explicit. Phase 0.5 read of `AcceptWelcomeAsync` would confirm. |
| MIP-02 §"Processing Requirements" step 4 — Store group information | `ManagedMlsService.cs:423` (`PersistGroupStateAsync`) + `MessageService.cs:1499-…` (chat creation) | **OK**. |
| MIP-02 §"Processing Requirements" step 5 — Rotate KeyPackage (publish fresh kind:30443 under same `d` tag) | `ManagedMlsService.cs:417-421` only logs `"rotation recommended"` | **Missing**. |
| MIP-02 §"Processing Requirements" step 6 — Securely delete init_key | only on logout (`Reset()`); no per-Welcome deletion | **Missing** for last_resort once rotation lands; **N/A** today since rotation never fires. |
| MIP-02 §"Processing Requirements" step 7 — Catch up on outstanding Commits, then self-update | not implemented | **Missing**. |
| MIP-02 §"Self-Update Timing" MUST — perform self-update within 24h of joining | `UpdateKeysAsync` exists but no scheduler | **Missing**. |
| MIP-02 §"Self-Update Timing" SHOULD — process outstanding Commits before self-update | not implemented | **Missing**. |
| MIP-02 §"Self-Update Timing" SHOULD — complete self-update before sending application messages | not implemented | **Missing**. |
| MIP-02 §"Error Handling" — on Welcome failure: retain KeyPackage, display clear errors, log details | `ManagedMlsService.cs:441-444` throws clear exception; KP not deleted | **OK**. |

### MIP-03 — Group Messages

| MIP citation | Scramble file/line | Status |
|---|---|---|
| MIP-03 §"Commit Message Race Conditions" — sending step 2: MUST NOT apply commit until at least one relay confirms | `NostrService.cs:2980-2993` returns eventId regardless of OK; `Mdk.cs:256-257` auto-merges before publish | **Violated** — central bug for this batch. |
| MIP-03 §"Commit Message Race Conditions" — sending step 3: only then apply locally | `Mdk.cs:256-257` (apply happens at MLS-add time, before publish) | **Violated** — same root cause as above. |
| MIP-03 §"Commit Message Race Conditions" — sending step 4: only then send associated Welcomes | `MessageService.cs:706-735` | **Violated** — same root cause. |
| MIP-03 §"Commit Message Race Conditions" — receiving: tiebreaker by earliest `created_at`, lex-smallest id | `dotnet-mls/.../MlsGroup.cs ProcessCommitCore` ignores `_pendingCommit`; no tiebreaker logic anywhere | **Missing**. |
| MIP-03 §"Commit Message Race Conditions" — SHOULD retain previous group states for fork recovery | not implemented | **Missing**. |
| MIP-03 §"Exception - Initial Group Creation" — first commit (epoch 0) MUST NOT be sent to relays | `MessageService.CreateGroupAsync` only creates local state via `_mlsService.CreateGroupAsync`; no commit publish at create time | **OK**. |
| MIP-03 §"Encryption Details" — ChaCha20-Poly1305 with `MLS-Exporter("marmot", "group-event", 32)` | `marmot-cs/.../Crypto/GroupEventEncryption.cs` (label `"marmot"`, context `"group-event"`, length 32, ChaCha20-Poly1305 via BouncyCastle); MDK derives via `mlsGroup.ExportSecret` at `Mdk.cs:488-491` | **OK**. |
| MIP-03 §"Encryption Details" — empty-byte AAD | `GroupEventEncryption.cs:51,116` (`Array.Empty<byte>()`) | **OK**. |
| MIP-03 §"Encryption Details" — random 12-byte nonce per event | `GroupEventEncryption.cs:44` (`RandomNumberGenerator.GetBytes(NonceSize)`) | **OK**. |
| MIP-03 §"Encryption Details" — content format = `base64(nonce \|\| ciphertext)` | `GroupEventEncryption.cs:60-64` | **OK**. |
| MIP-03 §"Encryption Details" — duplicate outbound nonce: MUST NOT transmit, SHOULD self-update | not implemented (no nonce tracking) | **Missing**. In practice astronomically unlikely with cryptographic RNG, but spec MUST. |
| MIP-03 §"Encryption Details" — RNG failure: MUST abort, MUST NOT use deterministic fallback | `RandomNumberGenerator.GetBytes` throws `CryptographicException` on failure; not silently swallowed | **OK** — by .NET BCL semantics. |
| MIP-03 §"Encryption Details" — fresh ephemeral keypair per event, never reuse | `ManagedMlsService.cs:524` (`BuildEphemeralSignedEvent` per event); not audited end-to-end | **Likely OK**. |
| MIP-03 §"Edge Cases" — invalid base64 → drop event without further processing | `GroupEventEncryption.cs:90-93` (throws `CryptographicException`) | **OK** at crypto layer; caller behavior on the exception not audited. |
| MIP-03 §"Edge Cases" — < 28 bytes content → reject | `GroupEventEncryption.cs:96-97` | **OK**. |
| MIP-03 §"Edge Cases" — AEAD authentication failure → drop, don't expose plaintext | `GroupEventEncryption.cs:127-130` (throws, never returns plaintext on failure) | **OK**. |
| MIP-03 §"Application Messages" — inner events MUST use sender's Nostr identity in `pubkey` | `ManagedMlsService.cs:506,554` (`{_publicKeyHex}` interpolated into rumor) | **OK** on outbound. |
| MIP-03 §"Application Messages" — clients MUST verify MLS sender matches inner event's pubkey | `ManagedMlsService.cs:623` extracts `senderHex = appMsg.Message.SenderIdentity` but **no comparison** with rumor's `pubkey` field | **Violated** — sender spoofing in inner rumor not detected. |
| MIP-03 §"Application Messages" — inner events MUST remain unsigned | rumor JSON at `ManagedMlsService.cs:506,554` has no `sig` field | **OK**. |
| MIP-03 §"Application Messages" — inner events MUST NOT include `h` tags or other group identifiers | `EncryptMessageAsync` callers pass `imeta` tags only; no `h` injection | **OK**. |
| MIP-03 §"Disappearing Messages" — auto-apply NIP-40 expiration tag when `disappearing_message_secs` is set | not implemented | **Missing** — out of scope for this batch. |
| MIP-03 §"Disappearing Messages" — when `disappearing_message_secs` is None: MUST remove caller-supplied expiration tag | not implemented | **Missing**. |
| MIP-03 §SelfRemove — full flow (proposal, accept-by-any-member commit, validation, etc.) | not implemented | **Missing** — declared future work. |
| MIP-03 §"Commit Messages — Who can commit" — admin verification on non-self-update / non-SelfRemove commits | relies on lower MLS layer; `MessageService.cs:529-532` extracts admins but doesn't validate inbound committer | **Likely Missing**. |
| MIP-03 §"Self-update Commits" — MUST contain only sender's Update proposal | `ManagedMlsService.UpdateKeysAsync` calls `_mdk.SelfUpdateAsync` (lower-layer enforces?) | **Likely OK** — assumes `dotnet-mls` `SelfUpdate` constructs commit with only the Update proposal. Phase 0.5 verify. |

### MIP-04 — Encrypted Media (optional)

| MIP citation | Scramble file/line | Status |
|---|---|---|
| MIP-04 §"Versioning" — current version `mip04-v2`; reject `mip04-v1` | `Crypto/Mip04MediaCrypto.cs` (file exists; version handling not read in detail) | **Unknown** — Phase 0.5 verify v1 rejection. |
| MIP-04 §"Key Derivation" v2 — `file_key = HKDF-Expand(exporter_secret, "mip04-v2" \|\| 0x00 \|\| ...)`, exporter from `MLS-Exporter("marmot", "encrypted-media", 32)` | `IMlsService.cs:147` references `"marmot"` + `"encrypted-media"` exporter; `ManagedMlsService.cs:1046` calls `_mdk.GetExporterSecret(groupId, "marmot", mediaContext, 32)` | **OK** (label/context match). HKDF context construction not line-checked. |
| MIP-04 §"Random Nonce Generation" v2 — random 12-byte nonce per encryption, store in `n` field | `Mip04MediaCrypto.cs` uses BouncyCastle ChaCha20-Poly1305; nonce handling not audited | **Likely OK** — file structure suggests compliance. Phase 0.5 verify. |
| MIP-04 §"Encryption Algorithm" — AAD = `"mip04-v2" \|\| 0x00 \|\| file_hash \|\| 0x00 \|\| mime \|\| 0x00 \|\| filename` | `Mip04MediaCrypto.cs` | **Unknown** — Phase 0.5 byte-layout verify. |
| MIP-04 §"Integrity Verification" — `SHA256(decrypted_content) == x_field_value` after decryption | not audited | **Unknown**. |
| MIP-04 §"imeta Tag Format" — required fields: `url`, `m`, `filename`, `x`, `n`, `v` | `MessageService.cs:322-396` builds imeta tags during media send | **Likely OK** — fields present in callsites; spec-conformance check needed. |

## Phase 0 audit findings

### Tally

Catalogued **76 distinct MUST/SHOULD requirements** across MIPs 00-04. Rough split:
- **OK** (compliant, code located): 26
- **Violated** (code exists and breaks the requirement): 9
- **Missing** (requirement is not addressed at all): 19
- **N/A** (doesn't apply to Scramble's role today): 3
- **Unknown** (cannot tell from code read alone — needs Phase 0.5 follow-up): 19

The "Unknown" pile is intentionally left as `Likely OK` / `Likely Missing` / `Unknown` rather than guessed, because guessing is what we're explicitly trying to stop doing.

### MIP-00 narrative

KeyPackage construction is **largely compliant**: extensions advertise `0xf2ee` and `0x000a`, proposals advertise self_remove, `i` tag is computed via the ciphersuite hash, `d` tag is randomly generated, encoding is `base64`, relays are populated by NostrService before signing. The big gap is **no rotation flow at all**: after a Welcome consumes a KeyPackage, the code logs `"rotation recommended"` (`ManagedMlsService.cs:421`) but never publishes a fresh `kind:30443` under the same `d` tag. This in turn means init_keys for last_resort KeyPackages are retained indefinitely — technically correct under MIP-00's last_resort retention rule, but wrong in spirit because the deletion side of the rotation→delete loop never fires. Selection of inbound KeyPackages for invitations is naive (`MessageService.cs:683`: `FirstOrDefault(IsCipherSuiteSupported)`) and **probably violates** MIP-00's prefer-non-last_resort + prefer-highest-`created_at` selection policy.

### MIP-01 narrative

Group construction uses the Marmot Group Data Extension correctly (created on `CreateGroupAsync`, read via `GetNostrGroupId` / `GetAdminPubkeys`). The MLS group ID stays local. **Two known gaps**: (a) `required_capabilities` does not include `self_remove` — already deferred as "Fix D" in `csharp-mls-fixes-plan.md` pending evidence; (b) **disappearing messages support is entirely absent** — `disappearing_message_secs`, NIP-40 expiration auto-application, validation that 0 is rejected — none of it is implemented. The compliance plan declared this out of scope for the current batch but it is a real MUST in MIP-01 / MIP-03 §"Disappearing Messages" and worth flagging as a follow-up batch. Image encryption code lives in `marmot-cs/.../Crypto/ImageEncryption.cs` and was not line-checked; a Phase 0.5 reading should verify the HKDF context labels (`"mip01-image-encryption-v2"`, `"mip01-blossom-upload-v2"`) match the spec exactly.

### MIP-02 narrative

The headline finding is the publish-then-merge ordering violation already documented (Welcome publish unconditional, no relay confirmation gate). Beyond that, the **post-Welcome processing flow is critically incomplete**: KeyPackage rotation, init_key deletion timing, catch-up-on-outstanding-commits, and the 24h MUST self-update — none of these have implementations. `UpdateKeysAsync` exists as an API surface but no scheduler invokes it. KeyPackage retention on Welcome failure is OK by accident (no deletion code = nothing to skip). The inner Welcome rumor structure and NIP-59 wrapping look correct on the publish side; receive-side validation of the 0xF2EE extension as part of `AcceptWelcomeAsync` is **not explicit** and should be verified in Phase 0.5.

### MIP-03 narrative

**The most violated MIP** by surface area — central to this batch. Sending-side: the publish-then-apply ordering, OK confirmation, and welcome-after-confirmation requirements are all violated by the same root code path (`Mdk.cs` auto-merges, `NostrService.PublishEventAsync` swallows OK timeout, `MessageService.AddMemberAsync` chains them in reverse order). Receiving-side: no tiebreaker, no fork-state retention. **Encryption itself is OK** — exact constants (`MLS-Exporter("marmot", "group-event", 32)`, empty AAD, 12-byte random nonce, base64(nonce||ciphertext+tag)), correct edge-case handling for invalid base64 / short content / AEAD failure, RNG failure properly aborts via `CryptographicException`. Inner-event hygiene is mostly correct (unsigned, no `h` tag injection, sender's pubkey in `pubkey` field) **except** that the inbound MLS-sender vs inner-pubkey verification is missing (`ManagedMlsService.cs:623` extracts but doesn't compare) — a real spoofing hazard. SelfRemove and proper admin-gating on inbound commits are not implemented; both flagged as future work but worth visible tracking.

### MIP-04 narrative

`Mip04MediaCrypto.cs` exists and the IMlsService surface uses the right exporter labels (`"marmot"` + `"encrypted-media"`, length 32), so the foundation is correct. The detailed byte-layout of the AAD (`"mip04-v2" || 0x00 || file_hash || 0x00 || mime || 0x00 || filename`), v1-rejection, integrity verification (`SHA256(decrypted) == x`), and the random-nonce-stored-in-`n`-field rules were not audited line-by-line in Phase 0; all "Unknown" rows for MIP-04 should be resolved in Phase 0.5 by reading `Mip04MediaCrypto.cs` end-to-end. No critical violations expected — MIP-04 is `optional` and the existing code looks intentional, just under-audited.

### Unaudited / "Phase 0.5" follow-up reads

The following code paths warrant a focused 1-2 hour read before drafting any patch:
1. `dotnet-mls/.../MlsGroup.cs ProcessCommitCore` — confirm whether identity-changing proposals are rejected (MIP-00) and whether admin-gating on inbound commits exists at this layer (MIP-01, MIP-03).
2. `marmot-cs/.../Mip02/WelcomeEventBuilder.cs` — verify exact tag set on the inner kind:444 rumor.
3. `marmot-cs/.../GroupDataExtension` (TLS serialization) — verify QUIC varint encoding and version=3 handling.
4. `marmot-cs/.../Crypto/ImageEncryption.cs` — verify HKDF context labels.
5. `Crypto/Mip04MediaCrypto.cs` — verify AAD byte layout and v1 rejection.
6. `NostrService.FetchKeyPackagesAsync` and the `MessageService.cs:683` selection — verify (or fix) MIP-00 selection policy.
7. `ManagedMlsService.AcceptWelcomeAsync` — verify 0xF2EE extension validation as part of accept.

These are not blockers for the patch phases as designed; they're targeted reads that would either upgrade `Likely OK` → `OK` or surface additional violations to fold into the patch.

## Phased work plan

Total estimate: **~14-19 days of focused work**, across `marmot-cs`,
`dotnet-mls` (light), and `Scramble.Core` + `Scramble.Diagnostics`. May
expand if Phase 0 surfaces compliance gaps not predicted above.

### Phase 0 — Compliance audit *(1-2 days)*

Read MIPs 00 and 01 (00 = KeyPackages, 01 = Marmot Group Data Extension)
to backfill the spec context; then walk every MUST/SHOULD across MIPs
00-04 against Scramble code. Output: this file's "Where Scramble
violates..." table fully populated, plus any new requirements discovered.

**Exit criterion:** every MIP MUST/SHOULD has a row with a code
location and a status (OK / Violated / Missing / N/A). No patch code
written until this is done.

### Phase 1 — MDK foundations: staging API *(2-3 days)*

Located in `marmot-cs/src/MarmotCs.Core/Mdk.cs`.

- Add public `StageAddMembersAsync` / `StageRemoveMembersAsync` /
  `StageSelfUpdateAsync` / `StageGroupContextExtensionsAsync` — return
  commit + welcome bytes without auto-merging
- `MlsGroup.ClearPendingCommit` already exists; expose
  `ClearPendingCommit(byte[] groupId)` at the MDK level
- Add `HasPendingCommit(byte[] groupId)` for caller introspection
- Mark existing eager-merge `AddMembersAsync` / `RemoveMembersAsync` /
  self-update / GCE methods `[Obsolete]` pointing to the staged variants
- Unit tests in `marmot-cs/tests/MarmotCs.Core.Tests` for stage→merge,
  stage→clear, double-stage rejection

### Phase 2 — MDK foundations: tiebreaker receiver *(1-2 days)*

Located in `marmot-cs/src/MarmotCs.Core/Mdk.cs`.

- Track per-pending metadata at stage time: our intended Nostr
  `event_id` and `created_at` (caller passes these in)
- Add `ProcessIncomingCommitAsync(groupId, commitBytes, nostrEventId, createdAt)`:
  - No pending → delegate to `MlsGroup.ProcessCommit`
  - Pending at same epoch → apply MIP-03 tiebreaker:
    - Incoming wins → `ClearPendingCommit`, process incoming, throw
      `RaceLostException(newEpoch)` so the caller can retry their op
    - Ours wins → discard incoming silently, surface `RaceWonNotice`
- Tests: both directions, exact tiebreaker boundary cases (equal
  timestamp + lex-tied ids, equal timestamp + different ids, different
  timestamps).

### Phase 3 — Scramble NostrService surface fixes *(1-2 days)*

Located in `Scramble.Core/Services/NostrService.cs`.

- `PublishCommitAsync` / `PublishWelcomeAsync` /
  `PublishGroupMessageAsync` MUST throw `PublishUnconfirmedException`
  when `LastPublishOkResult.accepted == false` after the OK timeout —
  mirror `PublishKeyPackageAsync`'s existing check at line 1175
- Per-epoch outbound nonce-uniqueness tracker per MIP-03; refuse
  duplicate outbound nonce, recommend self-update
- RNG-failure abort path

### Phase 4 — Scramble MessageService AddMember rewrite *(2 days)*

Located in `Scramble.Core/Services/MessageService.cs` and
`Scramble.Core/Services/ManagedMlsService.cs`.

Restructure `MessageService.AddMemberAsync` per MIP-03 §sending steps
1-4 + MIP-02 §timing:

```csharp
var staged = await _mlsService.StageAddMemberAsync(chatId, kp);
try
{
    await _nostrService.PublishCommitAsync(staged.CommitData, ..., staged.IntendedCommitEventId);
    // ↑ throws PublishUnconfirmedException on no-OK
    await _mlsService.MergeStagedAsync(chatId);
    // only now is local MLS state advanced — MIP-03 step 3
    await _storageService.SaveMessageAsync(dedupMarker(staged.CommitEventId));
    await _nostrService.PublishWelcomeAsync(staged.WelcomeData, ...);
    // ↑ throws PublishUnconfirmedException on no-OK
    await UpdateParticipantsAsync(chat, member);
}
catch (PublishUnconfirmedException ex) when (!staged.IsMerged)
{
    await _mlsService.ClearStagedAsync(chatId);
    throw new AddMemberFailedException(...);
}
catch (PublishUnconfirmedException ex) when (staged.IsMerged)
{
    // commit landed, welcome didn't — recoverable, surface a different exception
    throw new WelcomeUnconfirmedException(staged.WelcomeData, member, ex);
}
```

Apply same shape to `RemoveMemberAsync` and any future SelfRemove or
GroupContextExtensions code paths.

### Phase 5 — Scramble incoming commit routing *(1 day)*

Wherever kind:445 events land in the event processor, route through
`Mdk.ProcessIncomingCommitAsync` instead of raw `ProcessCommit`. Catch
`RaceLostException`; schedule retry of any in-flight op at the new
epoch.

### Phase 6 — MIP-02 post-Welcome flow *(2 days)*

Verify or implement:

- KeyPackage rotation — publish a fresh `kind:30443` under the same
  `d` tag after a Welcome consumes a key package
- `init_key` secure deletion — memory-zero + remove from local store
- Catch-up on outstanding commits before self-update on join (MIP-02
  §"Self-Update Timing" RECOMMENDED order)
- Self-update scheduler — MUST happen within 24h of join even if
  catch-up was partial

### Phase 7 — MIP-03 ancillary checks *(1 day)*

- Confirm `CreateGroupAsync` does NOT publish the epoch-0 commit
  (MIP-03 §"Initial Group Creation")
- Confirm group event encryption uses exactly
  `MLS-Exporter("marmot", "group-event", 32)` + empty AAD + 12-byte
  random nonce
- Confirm inner events are unsigned and `h`-tag-stripped per MIP-03
  §"Application Messages"

### Phase 8 — Test coverage *(2-3 days)*

All new and updated tests grounded in spec section names:

- **Invert** the two existing tests in
  `tests/Scramble.Diagnostics/RelayHarness/PublishFailureTests.cs` —
  they currently assert the bug; post-fix they assert the spec is
  upheld
- Fork-race test using two `MessageService` instances racing
  AddMember (proves MIP-03 tiebreaker)
- MIP-02 timing test: prove no Welcome publish happens without commit
  OK
- MIP-02 KP-rotation test: post-Welcome, fresh `kind:30443` was
  published with same `d` tag
- MIP-02 self-update test: 24h scheduler fires
- MIP-03 nonce-uniqueness test: deliberate duplicate nonce gets
  refused
- MIP-03 initial-group test: no `kind:445` published on group
  creation

### Phase 9 — Documentation + cleanup *(1 day)*

- Update this file with what shipped vs. what was punted
- Deprecation notices on old MDK methods, with migration notes
- If new conventions emerge for future work (e.g., always go through
  staged path), add to `CLAUDE.md` / `AGENTS.md`

## Open questions to resolve during Phase 0

- Does `marmot-cs` already track per-pending metadata, or do we need to
  add a parameter at stage time?
- How many call sites of the current eager-merge MDK methods exist?
  Beyond Scramble, are there other consumers we need to coordinate
  with?
- Do `dotnet-mls` and `marmot-cs` have stable release versions that
  Scramble pins, or does Scramble track HEAD via submodule?
- What does `dotnet-mls` expose for "give me the metadata I'd need to
  run the tiebreaker" — does ProcessCommit return enough to know we
  applied vs. would-have-applied?

## Coordination with concurrent in-flight work

`ai-tasks/csharp-mls-fixes-plan.md` and `ai-tasks/whitenoise-mdk-divergence-2026-05.md`
track a parallel thread focused on MDK 0.7.1 → 0.8.0 API drift and inbound
rumor-id verification (MDK PR #287). Status as of master `fc6b3c5`:

- **Fix A** done — `ManagedMlsService.cs` now computes proper NIP-01
  rumor ids for outbound application/reaction kind:9/7 rumors. Does NOT
  cover the commit publish path. **Phase 4 must ensure the staged-commit
  publish path also constructs canonical rumor ids** so MDK 0.8.0
  `verify_id` accepts our commits inbound.
- **Fix B** done — narrower predicate around commit-decrypt failures.
  Helpful for surfacing tiebreaker / race-lost errors in tests, no
  correctness change.
- **MDK pin bump** pending (native path stays on 0.7.1 as differential
  oracle for now). Out of scope for this batch.
- Fixes C/D explicitly deferred over there pending evidence; we don't
  re-scope them here.

Phase 0 (audit) MUST cross-reference both documents while building the
violation table. If a row is already addressed by a Fix A / B commit,
mark it accordingly rather than re-listing it as open work.

## Out of scope for this batch

- **Multi-relay publish coordination** — currently Scramble treats "any
  one relay accepted" as success. The MIP-03 spec says "at least one
  relay confirms receipt" so single-relay-OK is technically compliant,
  but in practice multi-relay quorum policies (e.g. 2-of-3) would be
  more robust. Future work.
- **Disappearing messages NIP-40 expiration handling** (MIP-03
  §"Disappearing Messages") — separate feature; audit now, fix later
- **Catch-up message ingestion at relay reconnect** — orthogonal to
  publish-then-merge; separate work
