# Push Notifications — Plan

## Status: Planning

## Goal

Deliver push notifications to OpenChat clients when the app is backgrounded or killed, without sacrificing the privacy properties of MLS + NIP-59 gift wrapping, and without forcing a hard dependency on Google/FCM for users who don't want it.

## Approach: Extend Transponder with a UnifiedPush transport

Rather than running two separate watcher servers (one for FCM, one for UnifiedPush) or adopting Amethyst's plaintext-endpoint UP server (which leaks pubkey↔endpoint mappings), fork and extend [Transponder](https://github.com/marmot-protocol/transponder) to add UnifiedPush as a second delivery transport alongside FCM/APNs.

The entire event pipeline is identical for both transports:

1. Subscribe to kind 1059 gift wraps addressed to the server's pubkey
2. Unwrap NIP-59 → extract kind 446 rumor → decrypt the 280-byte token
3. Deliver the wake-up to the device

Only the last hop differs:

| Transport | Delivery |
|-----------|----------|
| FCM / APNs | Google/Apple push with empty payload |
| UnifiedPush | `POST` to the user's ntfy endpoint URL with empty payload |

MIP-05's encrypted token format already has a `platform_byte` — we add a new value (e.g. `2 = UnifiedPush`) whose "token" bytes carry the endpoint URL instead of an FCM registration token. All other pieces — encryption, gift wrap, inbox discovery via kind 10050 — stay identical.

### Why this over a sidecar watcher

- **One binary, one deployment, one privacy model.** UnifiedPush inherits MIP-05's privacy guarantees: the server never sees sender, recipient, group membership, or content — only opaque gift wraps.
- **Fixes the Amethyst leak.** Amethyst's UP push server stores `pubkey → endpoint` in plaintext. Encrypted-token delivery closes that gap.
- **Upstreamable.** Worth proposing as a MIP-05 amendment. Clean fork is maintainable even if upstream declines.
- **One wire format for clients.** The only client-side variation is which platform byte to set and which token bytes to pack.

### Caveats

- **Rust.** Transponder is written in Rust. Learning curve, but small surface area — the additions are a new platform constant, an HTTP POST call, and config for ntfy.
- **Upstream tracking.** Occasional rebase/merge work against Marmot's Transponder.
- **OEM battery killers.** Still a problem for UnifiedPush on Xiaomi/Samsung/Huawei (the distributor's foreground service can be killed). Mitigations covered below.

---

## Server side: Transponder fork

### Deployment

- Rust binary, Docker-friendly, ~1 vCPU / 1 GB RAM sufficient.
- Config: TOML with server keypair, FCM service account JSON, relay list — extended with ntfy server URL(s) allowed for UP delivery.
- Publishes kind 10050 (inbox relay list) for client discovery.

### Extensions to Transponder

| Work item | Effort |
|-----------|--------|
| Add `platform_byte = 2` (UnifiedPush) to token decoder | Trivial |
| Interpret decrypted "token" bytes as UTF-8 endpoint URL | Trivial |
| HTTP client to POST empty body to endpoint URL | Small |
| Config field: optional allow-list of ntfy hosts (anti-abuse) | Small |
| Tests for the new platform path | Small |

**Effort:** ~1 week for a Rust newcomer (reading existing code, making small additions).

---

## Client side (OpenChat Android)

### Already implemented

- NIP-59 gift wrap create + unwrap (`NostrService.cs:1180-1343`)
- NIP-44 encryption/decryption (`MarmotCs.Protocol.Nip44`)
- HKDF-SHA256 (`Mip04MediaCrypto.cs`)
- ChaCha20-Poly1305 AEAD (`Mip04MediaCrypto.cs`, BouncyCastle)
- secp256k1 ECDH (`NBitcoin.Secp256k1`)
- MLS application message routing by inner kind (`MessageService.HandleGroupMessage`)
- `MlsDecryptedMessage.RumorKind` — receiving side can identify arbitrary inner kinds

### Must build

Shared work (regardless of transport):

| Work item | Effort | Details |
|-----------|--------|---------|
| `EncryptMessageAsync` kind parameter | Trivial | Add `int innerKind = 9` to `IMlsService`, `ManagedMlsService`, `MlsService`. Currently hardcoded to kind 9 at `ManagedMlsService.cs:406` |
| `HandleGroupMessage` routing | Small | Add cases for kind 447/448/449 before default kind 9 path |
| Token encryption | Small | 220-byte plaintext (platform + token/URL + padding) + ephemeral ECDH + HKDF(`"mip05-v1"`, `"mip05-token-encryption"`) + ChaCha20-Poly1305 → 280-byte encrypted token |
| Token storage | Small | SQLite table: `notification_tokens(group_id, leaf_index, encrypted_token, server_pubkey, relay_hint, updated_at)` |
| MLS inner kind 447 (token request) | Medium | On group join: encrypt token/endpoint with Transponder's pubkey, send as MLS application message |
| MLS inner kind 448 (token list response) | Medium | Gossip protocol: respond to 447 with all known tokens indexed by MLS leaf. 0-2s random delay, skip if someone already responded |
| MLS inner kind 449 (token removal) | Small | Send on group leave or notification disable. Remove sender's token from local store |
| Kind 446 construction + gift wrap | Medium | Collect tokens from storage, add decoys (50% self, 10-20% from other groups, min 3), shuffle, concatenate, base64-encode, wrap in 446 rumor → kind 13 seal → kind 1059 gift wrap → publish to Transponder's kind 10050 inbox relays |
| Hook into send flow | Small | After `EncryptMessageAsync` in `MessageService.SendMessageAsync`, build + publish kind 446 |
| Server discovery | Small | Fetch Transponder's kind 10050 event to find its inbox relays |
| Token lifecycle | Small-Med | Refresh every 25-35 days (randomized), prune >35 days, auto-remove when leaves are removed from MLS group |

FCM path (Play Store build):

| Work item | Effort | Details |
|-----------|--------|---------|
| FCM registration | Small | `Xamarin.Firebase.Messaging` NuGet, `FirebaseMessagingService` subclass |
| Wake handler | Medium | `FirebaseMessagingService.OnMessageReceived` → reconnect relays → fetch/decrypt → local notification |

UnifiedPush path (F-Droid / degoogled build):

| Work item | Effort | Details |
|-----------|--------|---------|
| `UnifiedPushReceiver.cs` | Small | Android `BroadcastReceiver` for `org.unifiedpush.android.connector.PUSH_EVENT` and registration intents |
| Registration flow | Small | Call distributor via Intent → receive endpoint URL → pack URL as MIP-05 token with `platform_byte = 2` |
| Wake handler | Medium | On push received: `goAsync()` + short-lived foreground service → reconnect relays → fetch/decrypt → local notification |
| Immediate generic notification | Small | Post "New message" instantly on push; update with sender/preview after decrypt. Guarantees user sees *something* even if decrypt is slow/killed |
| Re-registration | Small | Handle distributor changes, token refresh |

### Build variant strategy

Two Android build flavors:

- `PlayStore` — includes Firebase Messaging, registers an FCM token
- `FDroid` — no Google deps, UnifiedPush receiver only

Both use the same Transponder instance. The only difference is which `platform_byte` the client packs into its encrypted token.

---

## MIP-05 protocol summary (unchanged)

**Event kinds:**

| Kind | Name | Transport | Purpose |
|------|------|-----------|---------|
| 446 | Notification Request | NIP-59 gift wrap to Transponder | Carries encrypted device tokens |
| 447 | Token Request | MLS application message (inside kind 445) | Share your token on group join |
| 448 | Token List Response | MLS application message (inside kind 445) | Gossip all known tokens to requester |
| 449 | Token Removal | MLS application message (inside kind 445) | Remove token on leave/disable |

**Token encryption:**

1. Construct 220-byte plaintext: `[platform_byte | token_length_u16be | token_bytes | random_padding]`
   - `platform_byte = 0` → iOS APNs
   - `platform_byte = 1` → Android FCM
   - `platform_byte = 2` → UnifiedPush (token bytes = endpoint URL) *(extension)*
2. Generate ephemeral secp256k1 keypair (fresh per token)
3. `shared_x = ECDH(ephemeral_privkey, server_pubkey)`
4. `prk = HKDF-Extract(SHA256, salt="mip05-v1", IKM=shared_x)`
5. `key = HKDF-Expand(prk, info="mip05-token-encryption", len=32)`
6. `nonce = random_bytes(12)`
7. `ciphertext = ChaCha20-Poly1305(key, nonce, plaintext, aad="")`
8. Result: `[ephemeral_pubkey_32 | nonce_12 | ciphertext_236]` = 280 bytes fixed

---

## Delivery UX (UnifiedPush)

UnifiedPush is data-only — the distributor silently delivers an Intent; there is no "platform shows a notification for you" fallback. Our receiver must wake up, fetch from relays, decrypt MLS, and post a local notification itself.

**Recommended pattern:**

1. Post a generic "New message in OpenChat" notification **immediately** when the push Intent arrives.
2. Start a short-lived foreground service to do relay fetch + MLS decrypt (bypasses the 10-second BroadcastReceiver limit and is less likely to be killed).
3. Update the notification with sender name, group, and preview once decryption succeeds.

This guarantees the user always sees something, even if decrypt is slow or gets killed mid-flight.

**Known issues on Android:**

| Issue | Impact | Mitigation |
|-------|--------|-----------|
| OEM battery killers (Xiaomi/Samsung/Huawei) | High | `dontkillmyapp.com` onboarding guidance; short-lived foreground service |
| BroadcastReceiver 10-second limit | Medium | `goAsync()` + foreground service |
| App force-killed from recents | Medium | Foreground service launched from receiver |
| Slow relay response | Low-Med | Cache recent relay connection state; immediate generic notification |

---

## Rollout order

1. **Fork Transponder**, add UnifiedPush platform byte + ntfy POST delivery. Test end-to-end with a test client posting fake kind 446 events.
2. **Client wire format** — implement token encryption, kind 446 construction, gift-wrap publishing. Not yet hooked to any platform push.
3. **FCM path** — Play Store variant, FirebaseMessagingService, wake handler.
4. **UnifiedPush path** — F-Droid variant, BroadcastReceiver, distributor registration, wake handler.
5. **MLS inner kinds 447/448/449** — token gossip across group members.
6. **Lifecycle** — refresh, prune, removal on leave.
7. Consider upstreaming the UnifiedPush extension to Marmot/Transponder.

## Nostr ecosystem references

- **Amethyst** — UnifiedPush support in F-Droid build (v0.80.1+). Uses plaintext pubkey↔endpoint mapping server (`amethyst-push-notif-server`).
- **Pokey** — standalone Nostr notification app, can act as both UP distributor and relay watcher.
- **White Noise / Marmot Transponder** — FCM/APNs-only reference implementation we're extending.