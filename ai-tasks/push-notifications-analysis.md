# Push Notifications Analysis

## Status: Research / Planning

## Context

OpenChat Android has no push notification support. When the app is backgrounded, Android kills WebSocket connections and messages are missed until the user reopens the app (`MainActivity.OnResume` reconnects relays).

This analysis covers three approaches investigated, ordered from simplest to most complex.

---

## Option 1: Amber Notification Piggyback (Simplest)

**Concept:** Amber (NIP-46 external signer) already runs a background service with persistent relay connections and shows notifications for incoming Nostr events. Since MLS group messages arrive as kind 1059 gift wraps with the user's pubkey in the `p` tag, Amber may already see them.

**How it would work:**
- Amber subscribes to kind 1059 events for the user's pubkey (it already does this for DMs)
- Amber shows a notification like "new encrypted message"
- Tapping the notification opens OpenChat via Android Intent

**Advantages:**
- Zero implementation work on OpenChat's side
- No FCM, no Transponder server, no foreground service
- Amber already solves the hard Android problem (staying alive in background)
- Battery-friendly (no duplicate relay connections)

**Limitations:**
- No message preview — Amber can't decrypt MLS, so notifications are generic
- Depends on Amber being installed and configured (not all users use Amber)
- Requires Amber to support notification tap → open external app (may need Intent filter in OpenChat's AndroidManifest)
- No notification if user uses local keys instead of Amber
- OpenChat has no control over notification content, appearance, or behaviour

**Investigation needed:**
- [ ] Confirm Amber subscribes to kind 1059 for the user's pubkey
- [ ] Confirm Amber shows notifications for unrecognised gift-wrapped events
- [ ] Check if Amber supports deep-linking / opening external apps on notification tap
- [ ] Check if OpenChat can register an Intent filter for Nostr event kinds

**Effort:** Near zero (OpenChat side). May need AndroidManifest Intent filter only.

---

## Option 2: Android Foreground Service (Medium)

**Concept:** Run an Android foreground service in OpenChat that keeps relay WebSocket connections alive when the app is backgrounded. Post local notifications when kind 445 messages arrive.

This is what White Noise currently ships as their primary notification transport.

**What to build:**
- `RelayForegroundService.cs` — Android `Service` with `StartForeground()`, persistent "Connected to relays" notification
- `NotificationHelper.cs` — Create `NotificationChannel` (API 26+), post local notifications via `NotificationManager`
- Notification suppression logic: skip if message is from self, group is muted, or chat is currently open
- Hook into existing `MessageService` message observables

**Advantages:**
- Works for all users (local keys and Amber)
- Full control over notification content and appearance
- Can show message previews (messages are decrypted in-process)
- No external server dependency
- Moderate implementation effort

**Limitations:**
- Persistent notification required (Android foreground service policy)
- Battery impact — continuous WebSocket connections
- Android may still kill the service under memory pressure (Doze mode, OEM battery savers)
- No notifications when app is force-killed

**Effort:** ~1-2 weeks

---

## Option 3: MIP-05 Push Notifications via Transponder (Full Solution)

**Concept:** Privacy-preserving push notifications per the Marmot MIP-05 spec. A stateless relay server (Transponder) receives encrypted device tokens, decrypts them, and sends silent FCM pushes to wake the device. The app then connects to relays and fetches messages.

### Server Side (Transponder)

- Rust binary at `github.com/marmot-protocol/transponder`
- Docker deployment, 1 vCPU/1GB sufficient
- Subscribes to kind 1059 gift wraps addressed to its pubkey
- Unwraps NIP-59 → extracts kind 446 rumor → decrypts 280-byte tokens → sends silent FCM/APNs push
- Stateless — stores nothing persistently
- Config: TOML with server keypair, FCM service account JSON, relay list
- Publishes kind 10050 (inbox relay list) for client discovery

**Effort:** ~1 day to deploy

### Client Side (OpenChat)

#### Already implemented:
- NIP-59 gift wrap create + unwrap (`NostrService.cs:1180-1343`)
- NIP-44 encryption/decryption (`MarmotCs.Protocol.Nip44`)
- HKDF-SHA256 (`Mip04MediaCrypto.cs`)
- ChaCha20-Poly1305 AEAD (`Mip04MediaCrypto.cs`, BouncyCastle)
- secp256k1 ECDH (`NBitcoin.Secp256k1`)
- MLS application message routing by inner kind (`MessageService.HandleGroupMessage`)
- `MlsDecryptedMessage.RumorKind` — receiving side can identify arbitrary inner kinds

#### Must build:

| Work Item | Effort | Details |
|-----------|--------|---------|
| `EncryptMessageAsync` kind parameter | Trivial | Add `int innerKind = 9` to `IMlsService`, `ManagedMlsService`, `MlsService`. Currently hardcoded to kind 9 at `ManagedMlsService.cs:406` |
| `HandleGroupMessage` routing | Small | Add cases for kind 447/448/449 before default kind 9 path |
| FCM registration | Small | Add `Xamarin.Firebase.Messaging` NuGet, `FirebaseMessagingService` subclass |
| Token encryption | Small | 220-byte plaintext (platform + token + padding) + ephemeral ECDH + HKDF(`"mip05-v1"`, `"mip05-token-encryption"`) + ChaCha20-Poly1305 → 280-byte encrypted token |
| Token storage | Small | New SQLite table: `notification_tokens(group_id, leaf_index, encrypted_token, server_pubkey, relay_hint, updated_at)` |
| MLS inner kind 447 (token request) | Medium | On group join: encrypt FCM token with Transponder's pubkey, send as MLS application message. Others respond with kind 448 |
| MLS inner kind 448 (token list response) | Medium | Gossip protocol: respond to 447 with all known tokens indexed by MLS leaf. 0-2s random delay, skip if someone already responded |
| MLS inner kind 449 (token removal) | Small | Send on group leave or notification disable. Remove sender's token from local store |
| Kind 446 construction + gift wrap | Medium | Collect tokens from storage, add decoys (50% self, 10-20% from other groups, min 3), shuffle, concatenate to single byte array, base64-encode, wrap in 446 rumor → kind 13 seal → kind 1059 gift wrap → publish to Transponder's kind 10050 inbox relays |
| Hook into send flow | Small | After `EncryptMessageAsync` in `MessageService.SendMessageAsync`, build + publish kind 446 |
| Server discovery | Small | Fetch Transponder's kind 10050 event to find its inbox relays |
| Android wake handler | Medium | `FirebaseMessagingService.OnMessageReceived` → reconnect relays → fetch/decrypt messages → show local notification |
| Token lifecycle | Small-Med | Refresh every 25-35 days (randomized), prune >35 days, auto-remove when leaves removed from MLS group |

**Advantages:**
- Works when app is fully killed
- Privacy-preserving — Transponder learns nothing about content, sender, recipient, or group membership
- Silent push (no content in FCM payload) — message stays encrypted until device fetches from relay
- Interoperable with other Marmot/MLS clients (White Noise)
- Works for both Amber and local-key users

**Limitations:**
- Requires running a Transponder server
- Requires Firebase project (FCM credentials)
- Most complex option (~2-3 weeks client work + 1 day server)
- Rust native DLL (`MlsService`) may also need kind parameter fix if using Rust backend

### MIP-05 Protocol Summary

**Event kinds:**
| Kind | Name | Transport | Purpose |
|------|------|-----------|---------|
| 446 | Notification Request | NIP-59 gift wrap to Transponder | Carries encrypted device tokens |
| 447 | Token Request | MLS application message (inside kind 445) | Share your token on group join |
| 448 | Token List Response | MLS application message (inside kind 445) | Gossip all known tokens to requester |
| 449 | Token Removal | MLS application message (inside kind 445) | Remove token on leave/disable |

**Token encryption (per MIP-05):**
1. Construct 220-byte plaintext: `[platform_byte | token_length_u16be | token_bytes | random_padding]`
2. Generate ephemeral secp256k1 keypair (fresh per token)
3. `shared_x = ECDH(ephemeral_privkey, server_pubkey)`
4. `prk = HKDF-Extract(SHA256, salt="mip05-v1", IKM=shared_x)`
5. `key = HKDF-Expand(prk, info="mip05-token-encryption", len=32)`
6. `nonce = random_bytes(12)`
7. `ciphertext = ChaCha20-Poly1305(key, nonce, plaintext, aad="")`
8. Result: `[ephemeral_pubkey_32 | nonce_12 | ciphertext_236]` = 280 bytes fixed

---

## Recommendation

Start with **Option 1** (Amber piggyback) — investigate whether it works with zero effort. If Amber already notifies on kind 1059 events, this covers the Amber-user case immediately.

Then implement **Option 2** (foreground service) as the reliable baseline for all users.

**Option 3** (MIP-05) is the long-term goal for interoperability with the Marmot ecosystem, but is not urgent until there are more MLS clients to interoperate with.
