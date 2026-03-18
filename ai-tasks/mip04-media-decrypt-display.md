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

## User Flow

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

### Step 1: Add MIP-04 crypto utility
- [ ] Create `src/OpenChat.Core/Crypto/Mip04MediaCrypto.cs`
- [ ] Implement `DeriveMediaEncryptionKey(byte[] exporterSecret, string sha256, string mimeType, string filename)`:
  - `baseSecret = MLS-Exporter("marmot", "encrypted-media", 32)` — need to expose this from ManagedMlsService
  - `context = "mip04-v2" || 0x00 || file_hash_bytes || 0x00 || mime_type_bytes || 0x00 || filename_bytes || 0x00 || "key"`
  - `fileKey = HKDF-Expand(SHA256, baseSecret, context, 32)`
- [ ] Implement `DecryptMediaFile(byte[] encrypted, byte[] fileKey, string sha256, string mimeType, string filename, string nonceHex)`:
  - Build AAD: `"mip04-v2" || 0x00 || file_hash || 0x00 || mime_type || 0x00 || filename`
  - ChaCha20-Poly1305 decrypt with key + nonce + AAD
  - Verify `SHA256(decrypted) == sha256`
  - Return decrypted bytes

### Step 2: Expose media exporter secret from MLS service
- [ ] Add `GetMediaExporterSecret(byte[] groupId)` to `IMlsService` and `ManagedMlsService`
- [ ] Calls `mlsGroup.ExportSecret("marmot", "encrypted-media", 32)` — the underlying API already supports custom labels
- [ ] Stub in `MlsService` (Rust backend)

### Step 3: Add secure HTTP download service
- [ ] Create `src/OpenChat.Core/Services/MediaDownloadService.cs`
- [ ] `IMediaDownloadService` interface with:
  - `Task<long?> GetFileSizeAsync(string url)` — HEAD request
  - `Task<byte[]> DownloadAsync(string url, long maxSize, CancellationToken ct)` — streaming download with size check
- [ ] URL validation:
  - HTTPS only
  - Resolve DNS, block private/reserved IPs (10.x, 172.16-31.x, 192.168.x, 127.x, 169.254.x, ::1, fc00::/7)
- [ ] Known Blossom server check:
  - Maintain a list of known hostnames (configurable)
  - `bool IsKnownBlossomServer(string url)` — returns false for unknown hosts

### Step 4: Add media loading to ChatViewModel / MessageViewModel
- [ ] Add `LoadMediaCommand` to `MessageViewModel` (or a media-specific VM)
- [ ] Add reactive properties: `IsLoadingMedia`, `MediaBytes`, `IsMediaLoaded`, `MediaError`
- [ ] Add `IsUnknownServer` property (for warning display)
- [ ] Add `MediaSizeDisplay` property (e.g., "2.4 MB")
- [ ] Flow:
  1. User taps "Load" → `IsLoadingMedia = true`
  2. Check if known Blossom server — if not, show warning (could be a confirmation dialog or inline warning that was already visible)
  3. HEAD request → set `MediaSizeDisplay`
  4. If too large, prompt or reject
  5. Download → MIP-04 decrypt → set `MediaBytes`, `IsMediaLoaded = true`
  6. On error → set `MediaError`

### Step 5: Update MessageBubble UI
- [ ] Replace static "[Encrypted image: filename]" with interactive widget:
  - Before load: show filename + "Tap to load" button + IP warning text + unknown server warning (if applicable)
  - During load: show progress/spinner + size
  - After load: show decoded image inline (Avalonia `Image` control with `Bitmap` from stream)
  - On error: show error message
- [ ] GIFs: Avalonia supports animated GIFs natively — no special handling needed
- [ ] Image sizing: max width constrained to message bubble width, maintain aspect ratio

### Step 6: Memory cache
- [ ] Cache decrypted images in memory by message ID (avoid re-downloading on scroll)
- [ ] Clear cache on chat switch or when memory pressure is detected
- [ ] Don't persist decrypted images to disk (they're only in memory while viewing)

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
- [ ] Not started
