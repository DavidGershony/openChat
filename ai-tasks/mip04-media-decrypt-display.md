# MIP-04 Media Decryption & Display (Read-Only)

## Goal
Decrypt and display MIP-04 encrypted media (images, GIFs) received in group messages. Read-only — no upload/send support.

## Background
MIP-04 media attachments are encrypted blobs hosted on Blossom servers. The `imeta` tag in the rumor event contains the URL, filename, mime type, SHA-256, nonce, and version. The blob is ChaCha20-Poly1305 encrypted with a per-file key derived from the MLS exporter secret.

Currently OpenChat shows "[Encrypted image: filename]" placeholders. This task adds actual decryption and inline display.

## Security Analysis

### Attack vectors and mitigations

**1. SSRF — Malicious URL in imeta tag (HIGH risk)**
- Any group member can craft an `imeta` tag pointing to internal networks, localhost, cloud metadata endpoints
- **Mitigations:**
  - HTTPS only — reject `http://` URLs
  - DNS resolution + block private/reserved IP ranges (10.x, 172.16-31.x, 192.168.x, 127.x, 169.254.x, ::1, fc00::/7)
  - If URL hostname is NOT a known Blossom server, show warning to user: "This image is hosted on an unknown server. Download anyway?" with the hostname visible
  - Known Blossom servers list: configurable, seeded with common ones (blossom.band, etc.)

**2. IP leak / tracking pixel (MEDIUM risk)**
- Attacker hosts blob on their server, logs IPs of downloaders
- Defeats Nostr pseudonymity
- **Mitigation:** "Tap to load" button instead of auto-download. Button text includes: "Load image (your IP will be visible to the server)"

**3. Oversized blob / resource exhaustion (MEDIUM risk)**
- URL serves a huge file, fills disk/memory
- **Mitigations:**
  - Before downloading: HEAD request to get `Content-Length`
  - Show file size to user in the "Tap to load" button
  - If size > 10MB: prompt "This file is X MB. Are you sure?"
  - Hard limit: 50MB desktop, 20MB mobile — refuse to download above this
  - Streaming download with size check — abort if exceeds limit

**4. Malformed image / decoder exploit (LOW-MEDIUM risk)**
- Malformed images can exploit image parser vulnerabilities
- **Mitigations:**
  - Validate image magic bytes match expected mime type before rendering
  - Catch and discard decode errors gracefully
  - Don't render SVGs (potential script injection)

**5. Content tampering (LOW risk — well mitigated by protocol)**
- ChaCha20-Poly1305 AEAD authentication rejects tampered blobs
- SHA-256 integrity check after decryption (MIP-04 spec requirement)
- If either check fails, show error, don't render

## MIP-04 Toggle (Settings)

- Toggle switch in Settings: **"Enable media loading (MIP-04)"** — default OFF
- When OFF: image messages show "[Media disabled — enable in Settings]" in the chat bubble
- When ON: image messages show the tap-to-load flow described below
- Warning text next to the toggle in Settings:

> **Risk: Medium** — Enabling media loading allows the app to download files from URLs embedded in messages. This exposes your IP address to the hosting server and increases attack surface through network requests and image decoding. Only enable if you trust your group members.

- Store the setting in the user's local preferences (not synced to relays)
- Property: `bool IsMip04Enabled` on SettingsViewModel, persisted via StorageService

## User Flow (when MIP-04 enabled)

1. Image message arrives → show placeholder: "[Encrypted image: photo.jpg]" (current behavior)
2. Below placeholder, show: **"Tap to load (your IP will be visible to the host)"**
3. If URL is not a known Blossom server, additionally show: **"Unknown server: evil-server.com"** with warning icon
4. User taps → HEAD request to get file size
5. Show size: "Downloading 2.4 MB..."
6. If size > 10MB: prompt "This file is 15.2 MB. Download anyway?"
7. If size > hard limit: "File too large (52 MB). Maximum is 50 MB."
8. Download blob → MIP-04 decrypt → SHA-256 verify → render inline
9. Cache decrypted image in memory (don't re-download on scroll)

## Technical Implementation

### Step 0: Add MIP-04 toggle to Settings
- [x] Add `IsMip04Enabled` boolean property to `SettingsViewModel` (default: false)
- [x] Persist in a `AppSettings` table in SQLite (key-value store via `GetSettingAsync`/`SaveSettingAsync`)
- [x] Add toggle switch in `SettingsView.axaml` with risk warning text in Privacy section
- [x] Expose the setting to `MessageViewModel` so it can decide between "Media disabled" and "Tap to load"
- [x] When disabled: `MessageBubble` shows "Media loading disabled / Enable in Settings > Privacy"
- [x] When enabled: show the full tap-to-load flow

### Step 1: Add MIP-04 crypto utility
- [x] Create `src/OpenChat.Core/Crypto/Mip04MediaCrypto.cs`
- [x] Implement `DeriveMediaEncryptionKey(byte[] exporterSecret, string sha256, string mimeType, string filename)`:
  - `baseSecret = MLS-Exporter("marmot", "encrypted-media", 32)` — exposed from ManagedMlsService
  - `context = "mip04-v2" || 0x00 || file_hash_bytes || 0x00 || mime_type_bytes || 0x00 || filename_bytes || 0x00 || "key"`
  - `fileKey = HKDF-Expand(SHA256, baseSecret, context, 32)`
- [x] Implement `DecryptMediaFile(byte[] encrypted, byte[] fileKey, string sha256, string mimeType, string filename, string nonceHex)`:
  - Build AAD: `"mip04-v2" || 0x00 || file_hash || 0x00 || mime_type || 0x00 || filename`
  - ChaCha20-Poly1305 decrypt with key + nonce + AAD
  - Verify `SHA256(decrypted) == sha256`
  - Return decrypted bytes
- [x] Added `FileSha256`, `EncryptionNonce`, `EncryptionVersion` fields to `MlsDecryptedMessage` and `Message` model
- [x] Updated imeta parser in ManagedMlsService to extract `x` (sha256), `nonce`, `encryption-version` fields

### Step 2: Expose media exporter secret from MLS service
- [x] Add `GetMediaExporterSecret(byte[] groupId)` to `IMlsService` and `ManagedMlsService`
- [x] Uses reflection to access private `_groups` dict and call `ExportSecret("marmot", "encrypted-media", 32)`
- [x] Stub in `MlsService` (Rust backend) — throws `NotSupportedException`

### Step 3: Add secure HTTP download service
- [x] Create `src/OpenChat.Core/Services/IMediaDownloadService.cs` + `MediaDownloadService.cs`
- [x] `GetFileInfoAsync` (HEAD request), `DownloadAsync` (streaming with size check)
- [x] `ValidateUrlAsync`: HTTPS only, DNS resolution, private/reserved IP blocking
- [x] `IsKnownBlossomServer`: checks against list of known Blossom servers

### Step 4: Add media loading to ChatViewModel / MessageViewModel
- [x] Add `LoadMediaCommand` to `MessageViewModel`
- [x] Add reactive properties: `IsLoadingMedia`, `DecryptedMediaBytes`, `IsMediaLoaded`, `MediaError`, `MediaSizeDisplay`
- [x] Add `IsUnknownServer`, `ServerHostname`, `ShowMediaDisabled`, `ShowTapToLoad` properties
- [x] Full load flow: validate URL → HEAD → size check → download → derive key → decrypt → verify → cache
- [x] Image magic byte validation (blocks SVG)
- [x] Static service references from ChatViewModel (MediaDownloadService, MlsService, StorageService, cache)

### Step 5: Update MessageBubble UI
- [x] 5-state image widget: disabled / tap-to-load / loading / loaded / error
- [x] Both sent and received bubble variants updated
- [x] Unknown server warning with hostname displayed
- [x] IP visibility warning text
- [x] Image control with byte[] → Bitmap converter, max 400px, aspect ratio preserved
- [x] Reuses existing `PngBytesToBitmapConverter` (works for all image formats)

### Step 6: Memory cache
- [x] `Dictionary<string, byte[]> _mediaCache` in ChatViewModel
- [x] Check cache before download, store after successful decrypt
- [x] Clear cache in `ClearChat()`

## Files to create/modify
- New: `src/OpenChat.Core/Crypto/Mip04MediaCrypto.cs`
- New: `src/OpenChat.Core/Services/IMediaDownloadService.cs` + `MediaDownloadService.cs`
- `src/OpenChat.Core/Services/IMlsService.cs` (add GetMediaExporterSecret)
- `src/OpenChat.Core/Services/ManagedMlsService.cs` (implement GetMediaExporterSecret)
- `src/OpenChat.Core/Services/MlsService.cs` (stub)
- `src/OpenChat.Presentation/ViewModels/ChatViewModel.cs` (media loading in MessageViewModel)
- `src/OpenChat.UI/Controls/MessageBubble.axaml` (interactive image widget)

## Testing
- [ ] Mip04MediaCrypto unit tests: key derivation, decrypt, AAD construction, SHA-256 verify
- [ ] URL validation tests: HTTPS only, private IP blocking
- [ ] Integration test: full decrypt pipeline with known test vectors from marmot-ts
- [ ] UI: verify tap-to-load flow, size display, error handling

## Status
- [x] All steps implemented (Steps 0-6)
- [x] Build passes with 0 errors
- [x] All 198 tests pass (104+94, 3 skipped as expected)
- [x] Mip04MediaCrypto unit tests (26 tests in Mip04MediaCryptoTests.cs)
- [x] URL validation tests (HTTPS, private IP, known server checks)
- [ ] Integration test with known test vectors from marmot-ts (not yet available)
