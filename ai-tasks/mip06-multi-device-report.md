# MIP-06: Multi-Device Support — Full Report

> **Source**: [marmot-protocol/marmot PR #44](https://github.com/marmot-protocol/marmot/pull/44) (open, draft, optional MIP)
> **Branch**: `mip-06-multi-device` (head: `be63d35`, last touched Apr 13 2026)
> **Tracking issue**: [#43](https://github.com/marmot-protocol/marmot/issues/43)
> **Status**: 🚧 Draft — not yet merged into `master`. Approved by `dannym-arx`, with open review threads from `jgmontoya`, `vitorpamplona`, `futurepaul`.

---

## 1. Executive Summary

MIP-06 defines how a single Nostr identity can participate in the same Marmot/MLS group from **multiple devices simultaneously**. Each device is a fully independent MLS leaf node (own signing key, own encryption key, own state) but they all share the same Nostr pubkey in their `BasicCredential` identity field. New devices join existing groups via **MLS External Commits** (RFC 9420 §12.4.3.2), gated by a new MLS extension (`marmot_multi_device`, ID `0xF2F0`) and authorized by a three-part check:

1. Knowledge of the current epoch's **`group_event_key`** (the MIP-03 outer-encryption key).
2. Knowledge of the current epoch's **MIP-06 join PSK** (an exporter-derived External PSK).
3. A **Nostr identity proof** (a canonical unsigned `kind:450` event signed by the user's Nostr key) carried in `FramedContent.authenticated_data`.

The two MLS-secret values (`group_event_key`, `join_psk`) are transferred from an existing device to the new device through an **out-of-band, authenticated, X25519+HKDF+ChaCha20-Poly1305-encrypted pairing payload**.

History sync between devices is **explicitly out of scope** — a new device cannot read messages from epochs prior to its join (MLS forward secrecy).

---

## 2. Why This MIP Matters for Scramble

Scramble currently treats one Nostr identity ↔ one MLS leaf in a group. Without MIP-06:

- A user installing Scramble on a second device gets a fresh KeyPackage and a fresh leaf only if explicitly re-invited by an admin.
- "Linked device" flows (cf. `ai-tasks/completed/link-device-relay-selection.md`) cover relay/identity portability but not group membership.
- The `multi-device deployments` comments in `INostrService.cs:146` and `MessageService.cs:1755` already acknowledge stale-slot artifacts from de facto multi-device usage.

MIP-06 gives Scramble a standards-track way to:

- Let users add a desktop after a phone (or vice versa) and immediately participate in all their existing groups.
- Coalesce multiple leaves with the same Nostr pubkey into one logical user in chat UI.
- Cleanly remove a lost/compromised device.

Both UI targets (`Scramble.Android` + `Scramble.UI`) will need pairing UX, device-management views, and identity coalescing per project rules in `CLAUDE.md`.

---

## 3. Identity & State Model

| Property | MIP-06 rule |
|---|---|
| MLS signing key | Per-device, fresh |
| MLS encryption (HPKE) key | Per-device, fresh |
| Nostr pubkey (BasicCredential identity) | Same across all of a user's devices |
| MLS group state | **Fully independent** per device — no shared MLS state |
| Receiving messages | Each device decrypts independently |
| Sending messages | Sent message arrives on user's other devices as a normal group message |
| Pre-join history | **Cannot be decrypted** by a new device (forward secrecy) |
| Admin status | Identity-based (per `admin_pubkeys` in MIP-01) — every leaf for an admin identity inherits admin privileges |

Result: a user with 2 devices in a 5-person group occupies 2 of 6 leaves; both carry the same credential identity bytes.

---

## 4. The `marmot_multi_device` Extension (`0xF2F0`)

Wire format (TLS notation, QUIC-varint length prefixes per Marmot convention):

```tls
struct {
    uint16 version;  // current: 1; version 0 reserved & MUST reject
} MarmotMultiDevice;
```

A group is **MIP-06-enabled iff all three of these hold**:

1. `0xF2F0` present in `GroupContext.extensions`.
2. `0xF2F0` listed in `GroupContext.required_capabilities`.
3. **Every** current non-blank leaf advertises `0xF2F0` in `LeafNode.capabilities.extensions`.

If any one is missing, External Commits from `new_member_commit` senders **MUST be rejected**. This is the "signaling gate" — its purpose is to prevent silent state divergence between MIP-06-aware and MIP-06-unaware clients.

### Migration of an Existing Group

Strict ordering (matters because `required_capabilities` is consensus-critical):

1. Each MIP-06-capable member self-updates its LeafNode to advertise `0xF2F0`.
2. KeyPackages get re-published with `0xF2F0` advertised (also requires updating MIP-00: `mls_extensions` tag MUST include `0xf2f0` for MIP-06 implementations).
3. Admin only **after** every non-blank leaf advertises `0xF2F0` issues a `GroupContextExtensions` Commit that adds the extension and updates `required_capabilities`.
4. If even one current member can't advertise the capability, MIP-06 cannot be enabled until that member is removed/migrated.

---

## 5. External Commit Authorization (the new join path)

MIP-03 normally requires Commits to come from an admin. MIP-06 adds a **carve-out** for `new_member_commit` External Commits, but only inside a properly-signaled group.

An External Commit MUST be **accepted** when ALL of:

1. MIP-06 signaling gate is satisfied (the three checks above).
2. Joining LeafNode's credential identity (Nostr pubkey) matches an existing member's.
3. Includes exactly one MIP-06 join PSK proposal (External PSK, see §6).
4. Includes a valid Nostr identity proof in `FramedContent.authenticated_data` (see §7).
5. Contains **only** the required `ExternalInit` proposal + the MIP-06 join PSK `PreSharedKey` proposal — no `Remove`, no other proposal types. (Join-style only, never resync-style.)
6. Passes all standard MLS validation (RFC 9420 §12.4.2).

Validation order existing members MUST follow (deterministic, per spec):

1. Decrypt outer `kind:445` layer with current `group_event_key` (MIP-03).
2. Verify MLS `PublicMessage` structure & signature.
3. Check `marmot_multi_device` in `GroupContext.extensions`.
4. Check `0xF2F0` in `required_capabilities`.
5. Check every current non-blank leaf advertises `0xF2F0`.
6. Validate joining LeafNode (RFC 9420 §7.3).
7. Match credential identity to an existing member.
8. Verify Nostr identity proof in `authenticated_data`.
9. Verify exactly one PSK proposal with the right `psktype`/`psk_id`/`label`/`group_context_hash`/`psk_nonce` length.
10. Verify no other proposals are present.
11. `confirmation_tag` verification (implicitly proves PSK material is correct).

Any failure → reject.

### Cross-spec change in MIP-03

The PR amends `03.md` to add an explicit exception: when `sender_type == new_member_commit` AND `marmot_multi_device` is in `GroupContext.extensions`, apply MIP-06's rules instead of the default "non-admin commit → reject."

It also clarifies: for Commits other than MIP-06 External Commits, `FramedContent.authenticated_data` MUST be the empty byte string (unless another Marmot spec says otherwise).

---

## 6. The MIP-06 Join PSK (External PSK)

Purpose: cryptographically prove the new device received current-epoch group secret material from an existing device. The PSK is required to make `confirmation_tag` verification succeed.

### PSK ID structure

```tls
struct {
    opaque label[24];                  // ASCII("marmot-mip06-join-psk-v1")
    opaque group_context_hash[32];     // SHA-256(TLS-serialized GroupContext)
} MarmotMultiDeviceJoinPskId;
```

### PSK derivation

```text
serialized_GroupContext = TLS-serialize(GroupContext)
join_psk_id = TLS-serialize(MarmotMultiDeviceJoinPskId(
    label              = ASCII("marmot-mip06-join-psk-v1"),
    group_context_hash = SHA-256(serialized_GroupContext),
))
join_psk = MLS-Exporter("marmot-mip06-join-psk-v1", join_psk_id, KDF.Nh)
```

### `PreSharedKey` proposal contents

- `psktype = external` (1)
- `psk_id` = TLS-serialized `MarmotMultiDeviceJoinPskId` for the **current** `GroupContext`
- `psk_nonce` = fresh random `KDF.Nh`-byte value (NOT carried in the pairing payload — generated fresh per External Commit)

### Why exporter-derived rather than something simpler

- Existing members can derive `join_psk` without exposing the raw exporter secret.
- The PSK ID binds the secret to the precise `GroupContext` (group ID, epoch, tree hash, extensions).
- Fits real MLS stack APIs that resolve External PSKs through application storage.
- Proves "received current-epoch material from *some* member," but **does not** prove which leaf authorized — that property isn't required by MIP-06; identity binding comes from the Nostr proof.

Implementation note: stacks must register `join_psk` under exactly `join_psk_id` before constructing/processing the External Commit, and MUST NOT pass it as a Resumption PSK.

---

## 7. Nostr Identity Proof (in `authenticated_data`)

Solves: External Commits are self-signed by the joining device's MLS key, which is unrelated to the Nostr key. Without an extra binding, anyone with the pairing payload could claim any Nostr identity.

### Why `authenticated_data`

- App-defined opaque field, covered by the MLS signature (RFC 9420 §6).
- No extension negotiation needed.
- Doesn't disturb non-MIP-06 processing.

### Why a canonical Nostr event (instead of a raw signature)

- **Signer compatibility**: Most Nostr signers (NIP-46, Amber, etc.) only expose "sign a Nostr event," not "Schnorr-sign-arbitrary-bytes."
- **Deterministic preimage**: Fixed `kind`, `created_at=0`, `tags`, hex-encoded content → unambiguous bytes.
- **Domain separation**: Dedicated `kind: 450` + the `["m", "marmot-external-commit-auth-v1"]` tag.

### Construction (joining device)

1. Extract `credential_identity` (32-byte Nostr pubkey from BasicCredential) and `signature_key` (MLS sig pubkey) from the new device's `CredentialWithKey`.
2. TLS-serialize the `GroupContext` for the target epoch (taken from the `GroupInfo` in the pairing payload).
3. `challenge = SHA-256(ASCII("marmot-external-commit-v1") || credential_identity || signature_key || serialized_GroupContext)`
4. Build the canonical unsigned Nostr proof event:

   ```json
   {
     "kind": 450,
     "created_at": 0,
     "pubkey": "<joining user's nostr pubkey hex>",
     "tags": [["m", "marmot-external-commit-auth-v1"]],
     "content": "<lowercase-hex-of-32-byte-challenge>"
   }
   ```

   This event MUST NOT be published anywhere — it is purely a local signing template.
5. Sign it with standard Nostr event signing (the resulting 64-byte schnorr sig is what we carry).

### `authenticated_data` payload

```tls
struct {
    uint16 version;             // current: 1
    opaque nostr_event_sig[64]; // Nostr signature over the canonical proof event id
} NostrIdentityProof;
```

### Verification (existing members)

Reconstruct the same event using `pubkey`/`signature_key` from the joining LeafNode and `GroupContext` for `FramedContent.epoch`, then verify the Nostr signature over its event id.

### Security properties

- **Identity binding** to specific credential + signing key + group + epoch.
- **Replay prevention** via epoch + tree hash inside `GroupContext`.
- **Domain separation** prevents cross-protocol signature reuse.
- **Signer-friendly** — works with hardware/remote signers without exotic primitives.

---

## 8. Device Pairing Flow (out-of-band)

Three phases, channel-agnostic.

### Phase 1: Bootstrap key exchange

1. New device generates a fresh X25519 keypair (`new_ephemeral_priv`, `new_ephemeral_pub`).
2. New device shows `new_ephemeral_pub` (32 bytes) to the existing device (QR, NFC, BLE, manual entry, etc.).
3. May include channel hints (BLE info, LAN candidate, relay rendezvous token).

**Critical security requirement**: the bootstrap channel MUST be authenticated, OR there MUST be an explicit user-verifiable key confirmation (compare a fingerprint on both screens). Unauthenticated BLE advertisement alone is *not* sufficient — see threat T.13.10 (key substitution / MITM).

### Phase 2: Existing device builds and sends payload

1. User on existing device picks which groups to share (all / subset).
2. Existing device generates fresh `existing_ephemeral_priv` / `existing_ephemeral_pub` for this session.
3. For each selected group, it pulls:
   - `GroupInfo` (must include `external_pub` and `ratchet_tree` extensions)
   - current epoch `group_event_key` (the exact 32-byte MIP-03 outer key)
   - current epoch `join_psk`
4. Build TLS-serialized `PairingPayload` (see §9).
5. Encrypt:

   ```text
   shared_secret = X25519(existing_ephemeral_priv, new_ephemeral_pub)
   abort if shared_secret is all-zero
   prk        = HKDF-SHA256-Extract(salt=ASCII("marmot-pairing-v1"), IKM=shared_secret)
   key        = HKDF-SHA256-Expand(prk, ASCII("marmot-pairing-key"), 32)
   nonce      = Random(12)
   aad        = ASCII("marmot-pairing-v1") || new_ephemeral_pub || existing_ephemeral_pub
   ciphertext = ChaCha20-Poly1305.encrypt(key, nonce, payload, aad)
   wire       = existing_ephemeral_pub || nonce || ciphertext
   ```
6. Transfer.
7. Securely delete `existing_ephemeral_priv` and any derived keys.

### Phase 3: New device decrypts, validates, publishes

1. Parse `existing_ephemeral_pub || nonce || ciphertext`.
2. Recompute `shared_secret` with `new_ephemeral_priv`; abort if all-zero; derive same `key`/`aad`; decrypt.
3. **Per-group validation before constructing any External Commit**:
   - `GroupInfo.group_context.extensions` contains valid `marmot_multi_device` (`0xF2F0`).
   - `required_capabilities` requires `0xF2F0`.
   - Every current non-blank leaf advertises `0xF2F0`.
   - `GroupInfo` includes `external_pub` and `ratchet_tree` extensions.
   - `marmot_group_data` (extension `0xF2EE` from MIP-01) present and valid; its `nostr_group_id` is what goes in the `kind:445` `h` tag.
   - `len(group_event_key) == 32`.
   - `len(join_psk) == KDF.Nh` for the group's ciphersuite.
4. For each valid entry:
   - Generate fresh MLS signing key, build credential with the user's Nostr pubkey.
   - Construct the External Commit using the provided `GroupInfo`.
   - Compute `join_psk_id` from `GroupContext`; register `join_psk` as an External PSK under that ID; include exactly one MIP-06 join PSK `PreSharedKey` proposal with a fresh `psk_nonce`.
   - Put Nostr identity proof in `authenticated_data`.
   - Wrap in a `kind: 445` event encrypted with `group_event_key` (standard MIP-03 outer encryption, fresh ephemeral Nostr keypair for the event).
   - Publish to that group's relays using the `nostr_group_id` from `marmot_group_data`.
5. Securely delete `new_ephemeral_priv` and all retained pairing secrets after session completes/aborts.

### Retry handling (epoch drift)

Each per-group attempt ends as `success`, `stale_epoch`, or `fatal_error`. Only `stale_epoch` is retryable.

Recommended session model:

1. Bootstrap on a low-bandwidth channel (e.g., QR).
2. Upgrade to bidirectional bandwidth (BLE/LAN/relay rendezvous).
3. New device reports per-group `stale_epoch` failures.
4. Existing device sends a fresh Phase-2-style message **for the failed groups only**, reusing the original `new_ephemeral_pub` but generating a **new** `existing_ephemeral_priv/pub` per refresh and securely deleting it after each transfer.
5. New device retries only those groups, building a brand-new External Commit (new ExternalInit, new PSK nonce, new identity proof bound to the now-current GroupContext).

Stale `GroupInfo`/`group_event_key`/`join_psk` MUST NOT be reused. If no follow-up channel exists, the whole pairing must be restarted for the failed groups.

---

## 9. Pairing Payload (wire format)

```tls
struct {
    opaque group_event_key[32];     // exact MIP-03 outer key
    opaque join_psk<1..255>;        // length MUST equal KDF.Nh
    opaque group_info<1..2^32-1>;   // TLS-serialized GroupInfo
} GroupPairingDataV1;

struct {
    uint16 version;                          // current: 1; 0 reserved
    GroupPairingDataV1 groups<1..2^32-1>;
} PairingPayload;
```

- Variable-length vectors use **QUIC-style varint length prefixes** (Marmot convention from MIP-01) — implementations MUST NOT use RFC-8446 fixed-width prefixes.
- `nostr_group_id`, `group_id`, `epoch`, ciphersuite, etc. are **not** carried as separate fields — they are derived from the embedded `GroupInfo`/`marmot_group_data`.
- `psk_id` is derivable from `GroupContext`; only `join_psk` (the value) is transferred.

### Approximate sizes (ciphersuite `0x0001`, `KDF.Nh = 32`)

Per-group ≈ `491 + 288 * N` bytes, where `N` = group member count. (Ratchet tree dominates: ~288 B/member.)

| Scenario | Groups | Avg members | Per-group | Total | After ~30% compression |
|---|---|---|---|---|---|
| Minimal | 1 | 5 | ~1.9 KB | ~1.9 KB | ~1.4 KB |
| Light user | 5 | 10 | ~3.3 KB | ~16.5 KB | ~11.5 KB |
| Typical user | 10 | 20 | ~6.1 KB | ~61 KB | ~43 KB |
| Power user | 30 | 30 | ~8.9 KB | ~268 KB | ~188 KB |
| Heavy user | 50 | 50 | ~14.5 KB | ~728 KB | ~510 KB |

### Channel suitability

| Channel | Practical capacity | Notes |
|---|---|---|
| Single QR | ~2 KB | One small group only |
| BBQr (animated QR) | ~250 KB | Air-gapped |
| BLE | ~1 MB | Proximity, bidirectional, retry-friendly |
| LAN P2P | effectively unlimited | Highest throughput |
| Encrypted relay rendezvous | relay-limited | Works at distance |

Spec recommendation: support **at least one air-gapped** channel and **at least one bidirectional** channel for retries.

---

## 10. Optional Device Name Extension (`0xF2EF`)

Per-LeafNode optional extension to label devices in self-management UIs.

- Identifier: `0xF2EF`. Implementations supporting it MUST list it in `LeafNode.capabilities.extensions`.
- Names are **encrypted with NIP-44 using the user's own Nostr keypair** so only the device owner can read them:

  ```text
  conversation_key = NIP44.derive_conversation_key(nostr_privkey, nostr_pubkey)
  encrypted_name   = NIP44.encrypt(conversation_key, device_name_utf8)
  ```

- TLS struct: `opaque encrypted_device_name<1..2^16-1>`.
- Names SHOULD be ≤ 64 UTF-8 chars (e.g. "iPhone", "Desktop").
- Other clients without `0xF2EF` ignore the extension (RFC 9420 §13.4 — unknown LeafNode extensions are ignored). No group-wide negotiation required, hence "optional."
- Renaming = self-update Commit with new value.

---

## 11. Client UX Recommendations (SHOULD-level)

- **Identity coalescing**: collapse multiple leaves with the same Nostr pubkey into one logical user in chat lists; show "(2 devices)" or similar. Provide a separate device-management view that shows each leaf with join time and admin inheritance.
- **Own-message handling**: recognize echoes from your own other devices and render them as sent (not received); de-duplicate notifications.
- **Device-add visibility**: surface successful MIP-06 External Commits as explicit "device added" events (not silent background sync). If the joining identity is in `admin_pubkeys`, highlight that the new leaf inherits admin. Also surface unusual same-identity rapid joins / epoch churn to help detect compromise.
- **Device removal**:
  - "Leave this device" → SelfRemove targeting only the current leaf.
  - "Leave all my devices" → Remove every same-identity leaf in the group.
  - **SelfRemove is leaf-scoped** in MIP-06 groups (changed from MIP-03 default!) — it only removes the sending leaf. If sibling same-identity leaves remain, clients MUST warn and MUST NOT present this as "left the group."
  - This MIP does NOT define a stable cross-group device identifier or a non-admin removal carve-out for sibling leaves; cross-group removal is best-effort.
- **Forward-secrecy disclosure**: warn users that newly added devices cannot read pre-join messages.

---

## 12. Cross-MIP Spec Changes Bundled in PR #44

The PR also touches other MIP files for consistency:

- **`00.md` (MIP-00, KeyPackages)**:
  - MIP-06 implementations MUST include `0xF2F0` in the KeyPackage `mls_extensions` tag (in addition to existing `0x000a` and `0xf2ee`).
  - `relays` tag clarified as also used for KeyPackage **deletion** (not only Welcome delivery).
- **`01.md` (MIP-01, Group Construction)**:
  - Adds MIP-06 capability requirement when multi-device External Commit behavior is enabled.
  - Clarifies that Marmot uses **TLS notation** but with **QUIC varint length prefixes**, not RFC-8446 fixed-width prefixes (Marmot wire-format convention).
- **`03.md` (MIP-03, Group Messages)**:
  - Renames `encryption_key` / `exporter_secret` → **`group_event_key`** (now derived directly from MLS-Exporter as the 32-byte outer key, no extra step).
  - Adds the explicit MIP-06 carve-out for `new_member_commit` Commits.
  - Clarifies leaf-scoped SelfRemove behavior in MIP-06 groups.
  - Requires `authenticated_data` to be empty for non-MIP-06 Commits.
- **`threat_model.md`**: adds T.13.8 (epoch-refresh races), T.13.9 (External Commit flooding / epoch churn DoS), T.13.10 (Phase-1 bootstrap key substitution / MITM); CRITICAL validation checklist for MIP-06.

---

## 13. Threat Model Summary (multi-device-specific)

| ID | Threat | Mitigation |
|---|---|---|
| T.13.6 | Single-device compromise reads from join epoch onward | Remove device immediately; remaining devices self-update Commit |
| T.13.7 | Cross-device correlation (same credential ID across leaves reveals one user) | Inherent to model; consider device-name privacy |
| T.13.8 | Epoch-drift race during pairing | Per-group incremental retry; never reuse stale entries |
| T.13.9 | External Commit flooding / epoch-churn DoS | Signaling gate + admin visibility of repeated same-identity joins |
| T.13.10 | Phase-1 bootstrap key substitution (MITM on QR/BLE) | MUST use authenticated channel **or** explicit user-verified fingerprint |

Other notable points:

- The pairing payload is the highest-value secret in the protocol. If exposed, the existing-device-secrets channel collapses (an attacker with the payload still needs the Nostr signing capability + correct group signaling, but the two MLS-secret checks both fail open together).
- If a pairing session is canceled/leaked, implementations SHOULD advance the epoch in each affected group before re-pairing to invalidate transferred `group_event_key` / `join_psk`.
- Admin privilege inheritance is automatic and identity-based — every new leaf for an admin identity is an admin. Make it visible.
- **No protocol-level per-user device cap in v1** — implementations MUST NOT reject otherwise-valid joins on local count limits (would cause clients to disagree on group state). A future extension version can add a consensus-enforced cap.

---

## 14. Open Issues / Review Discussion (from PR #44)

- **vitorpamplona** raised: Nostr culture is "users test many clients on the same nsec." If a user is in a group with 5 people who each rotate clients weekly, security is only as good as the weakest tested client. Erskin acknowledges this is hard to fully solve and notes it's an argument for a dedicated "Marmot signer."
- **futurepaul** raised: device-name extension doesn't help with "newest KeyPackage gets all the invites" UX problem. Erskin notes the team is strongly considering moving KeyPackages to **replaceable events** (one KP per client at a time, easier deletion). This would be a separate MIP-00 amendment.
- **jgmontoya** asked about a per-user device cap. Resolution: explicitly NO cap in v1 (consensus-critical if added later); can be added by bumping `MarmotMultiDevice.version`.
- **alltheseas** confirmed the cross-spec MIP-03 carve-out wording is now in the PR.
- HKDF hash function pinning was raised earlier and addressed (now explicitly **HKDF-SHA256** throughout pairing).

---

## 15. Implementation Implications for Scramble

### 15.1 Native / Rust layer (`Scramble.Native`, `lib/marmot-cs`, MDK)

- **MDK pin**: MIP-06 needs MDK support for External Commits with External PSKs and `authenticated_data`. Current pin is `0babfa76` (~v0.7.1). Need to verify whether 0.8.0+ exposes the necessary APIs and `authenticated_data` handling. Likely a follow-up to the existing `csharp-mls-fixes-plan.md` (MDK pin bump is task in flight).
- **External PSK registration**: `ManagedMlsService` (and the Rust path) must be able to register an External PSK keyed by an arbitrary `psk_id` blob before processing/constructing a Commit.
- **Exporter access**: already partially in place — `ManagedMlsService` uses reflection to call `ExportSecret("marmot", "encrypted-media", 32)` (per `mip04-media-decrypt-display.md`). Need a clean API surface that supports both MIP-03 (`marmot`/`group-event`) and MIP-06 (`marmot-mip06-join-psk-v1`/<psk_id>) exporter labels.
- **Rename**: `exporter_secret` / `encryption_key` → `group_event_key` in MIP-03 paths to match the spec (audit-friendly, also matches PR #48).

### 15.2 Protocol / Marmot-CS layer (`lib/marmot-cs`)

New components needed under `MarmotCs.Protocol.Mip06`:

- `MarmotMultiDeviceExtension` (encode/decode `0xF2F0`).
- `MarmotMultiDeviceJoinPskId` (build + serialize TLS struct).
- `JoinPsk` derivation helper (MLS-Exporter wrapper).
- `NostrIdentityProof` builder & verifier (canonical `kind:450` event construction; depend on existing NIP-01 event hashing/signing).
- `PairingPayload` encoder/decoder (TLS notation + QUIC varints — `tls_codec`-compatible).
- `PairingChannel` encryption: X25519 + HKDF-SHA256 + ChaCha20-Poly1305 helper (mostly already present in NIP-44 / MIP-05 token flows; needs new domain-separation labels).
- `EncryptedDeviceName` extension (`0xF2EF`) wrapping NIP-44.

Validation:

- New "MIP-06 signaling gate" check (used both when constructing pairing payloads on the existing device and when accepting External Commits on receiving members).
- External Commit validator that runs the 11-step ordered checklist.

### 15.3 Service layer (`Scramble.Core`)

- **`ManagedMlsService`** / **`MessageService`**:
  - Surface `group_event_key` and `join_psk` to a new pairing service.
  - Accept inbound External Commits and route them through the MIP-06 validator before applying.
  - Enable MIP-06 on a group (admin-side `GroupContextExtensions` Commit) — pre-flight every leaf's capability.
- **`NostrService`** / `KeyPackage` builder: include `0xF2F0` in `mls_extensions` tag (and corresponding LeafNode capabilities) when MIP-06 support is on.
- **New `DevicePairingService`** in `Scramble.Core`:
  - Existing-device side: select groups, build payload, encrypt, generate channel artifact (QR / BLE / relay rendezvous), handle refresh/retry.
  - New-device side: scan / receive, decrypt, validate, build External Commits, publish, report per-group results.
- **Storage**: per-device key material (signing/HPKE keys) was already per-instance; need a new "joined as device" provenance flag and probably a per-leaf device-name cache.

### 15.4 Persistence

- Already MLS state is per-app-instance (one device = one DB), so no schema change for *being* multi-device. But:
  - Track which leaves in each group are "mine" (by credential identity) and any local device-name decryption.
  - Store admin-list visibility metadata for the "device added → got admin" highlight.

### 15.5 UI (must touch BOTH `Scramble.Android` AND `Scramble.UI`)

Per `CLAUDE.md`, every UI feature lands in both projects. New surfaces:

- **Pair a new device** (existing device flow): group picker, QR display + animated BBQr fallback, BLE/LAN handoff, refresh-on-retry, secrets-cleanup-on-cancel.
- **Add this device to my account** (new device flow): scanner + manual ephemeral-key entry; identity-import precondition (Nostr key must already be on the new device); progress UI per group; per-group success/`stale_epoch`/`fatal_error` reporting.
- **Device management view** per account / per group: list of leaves with same credential identity, decrypted name, joined timestamp, admin badge, "remove this device" action (admin-side Remove Commit).
- **Identity-coalesced chat list / message view**: collapse same-identity leaves into one user; treat own-other-device messages as sent (de-duplicate notifications).
- **Device-added / admin-inherited banners** in the group timeline.
- **Forward-secrecy notice** on first launch of a new device joined to an existing group.

### 15.6 Tests

- Unit: TLS round-trips for `MarmotMultiDevice`, `MarmotMultiDeviceJoinPskId`, `PairingPayload`, `NostrIdentityProof`, `EncryptedDeviceName`.
- Vector tests: known `group_event_key`, `join_psk`, `challenge` from MDK / marmot-ts (once they exist).
- Validator: each of the 11 ordered checks rejects when its precondition fails.
- End-to-end (headless): existing-device builds payload → new-device decrypts → External Commit → existing members accept → new device decrypts subsequent message. Add an `epoch drifts mid-pairing → stale_epoch → refresh succeeds` case. (The existing `HeadlessMessagingExtendedTests` already documents "multi-device sync"; this is where MIP-06 e2e tests would live.)
- Negative: missing `0xF2F0` in any of the three places; mismatched credential identity; PSK label/length wrong; identity proof wrong epoch / wrong key; extra proposal in External Commit; expired pairing payload.

---

## 16. Recommended Next Steps for Scramble

1. **Wait for upstream merge or pin a known commit** of `mip-06-multi-device`. Spec is still under review (last activity Apr 13 2026; one approval, one open conversation about device caps resolved).
2. **MDK readiness audit** — confirm the pinned MDK rev exposes:
   - External Commit construction with `authenticated_data`.
   - Application-resolved External PSK registration at arbitrary `psk_id`.
   - `MLS-Exporter` access with arbitrary label/context arguments.
   If not, this blocks implementation behind the existing MDK-pin-bump task.
3. **Land MIP-03 rename to `group_event_key`** in marmot-cs first (small, also tracked in PR #48 upstream) so MIP-06 derivation labels line up.
4. **Prototype `marmot-cs/Mip06`** modules independently of UI — identity-proof construction/verification can be vector-tested without any group state.
5. **Design pairing transport story** — start with an air-gapped BBQr (works today on both desktop and Android with a camera) plus a relay-rendezvous fallback for retries; defer BLE.
6. **Coordinate with `link-device-relay-selection.md`** — that flow ships the Nostr identity to the new device; MIP-06 picks up where it leaves off.
7. **Consider replaceable-KeyPackages MIP-00 amendment** in parallel — futurepaul's UX concern about KP routing affects multi-device usability significantly.

---

## 17. References

- PR: <https://github.com/marmot-protocol/marmot/pull/44>
- Tracking issue: <https://github.com/marmot-protocol/marmot/issues/43>
- Branch: <https://github.com/marmot-protocol/marmot/tree/mip-06-multi-device>
- Spec (raw): <https://raw.githubusercontent.com/marmot-protocol/marmot/mip-06-multi-device/06.md>
- Related upstream PRs:
  - #48 — replace NIP-44 with ChaCha20-Poly1305 for `kind:445` (overlaps with `group_event_key` rename)
  - #54 — KeyPackages → addressable kind 30443 (conflicts with current MIP-00 in scramble)
  - #63 — MIP-03 minimum encrypted content length
- Local Scramble files relevant to follow-up:
  - `ai-tasks/csharp-mls-fixes-plan.md` (MDK pin bump status)
  - `ai-tasks/whitenoise-mdk-divergence-2026-05.md` (MDK 0.7.1 → 0.8.0 gap)
  - `ai-tasks/completed/link-device-relay-selection.md`
  - `ai-tasks/completed/mip00-keypackage-compliance.md` (will need `0xF2F0` added to `mls_extensions`)
  - `ai-tasks/completed/mip04-media-decrypt-display.md` (existing reflection-based exporter access pattern)
  - `ai-tasks/TESTING_GUIDELINES.md` §9 (`HeadlessMessagingExtendedTests` — placeholder for multi-device e2e)
