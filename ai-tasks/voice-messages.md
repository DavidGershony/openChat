# Voice Messages: Record, Send, Receive, Play

## Goal
Full voice message support: record audio, encode to Opus, encrypt via MIP-04, upload to Blossom, send as kind 445 group message, receive and play back.

## Architecture

### Send Flow
1. User holds record button → capture PCM audio via platform API
2. Encode to Opus via Concentus (pure .NET) → .opus file in memory
3. MIP-04 encrypt: derive per-file key from MLS exporter secret, ChaCha20-Poly1305
4. Upload encrypted blob to Blossom server (HTTP PUT /upload with NIP-98 auth)
5. Build imeta tag: url, m=audio/opus, x=sha256, n=nonce, v=mip04-v2, filename
6. Send as MLS-encrypted kind 445 group message

### Receive Flow
1. Receive kind 445 → MLS decrypt → parse imeta tag with m=audio/opus
2. Show audio player widget (play button, duration, waveform placeholder)
3. User taps play → download encrypted blob from Blossom URL
4. MIP-04 decrypt → Opus decode via Concentus → play PCM audio

## Implementation Steps

### Step 1: Add NuGet packages and MessageType.Audio
- [ ] Add Concentus + Concentus.OggFile to OpenChat.Core
- [ ] Add MessageType.Audio to enum
- [ ] Add IsAudio computed property to MessageViewModel
- [ ] Add audio duration field to Message model

### Step 2: Blossom upload service
- [ ] Create IMediaUploadService interface
- [ ] Implement BlossomUploadService: PUT /upload with NIP-98 signed auth header
- [ ] NIP-98 auth: sign kind 24242 event with SHA-256 hash of blob, expiration tag
- [ ] Return upload result: URL, SHA-256 hash
- [ ] Add Blossom server URL to settings (default: blossom.nostr.build)
- [ ] Add Blossom server management UI in Settings (both Desktop and Android)

### Step 3: Audio recording service
- [ ] Create IAudioRecordingService interface: StartRecording, StopRecording, CancelRecording
- [ ] Desktop: platform-specific recording (WASAPI on Windows, PulseAudio/ALSA on Linux, CoreAudio on Mac)
- [ ] Android: MediaRecorder with OGG/Opus output format
- [ ] Return PCM samples or encoded audio bytes
- [ ] Opus encoding via Concentus (48kHz or 16kHz mono, Application.Voip, 24kbps)

### Step 4: Audio playback service
- [ ] Create IAudioPlaybackService interface: Play(byte[] pcmOrOpus), Stop, Pause, Position, Duration
- [ ] Desktop: platform audio output (WASAPI/PulseAudio/CoreAudio)
- [ ] Android: MediaPlayer or AudioTrack
- [ ] Opus decoding via Concentus

### Step 5: Send voice message flow
- [ ] Add RecordVoiceCommand to ChatViewModel
- [ ] Record → Opus encode → compute SHA-256 → MIP-04 encrypt → Blossom upload
- [ ] Build imeta tag with audio metadata
- [ ] Create MLS rumor with imeta tag → encrypt → build kind 445 → publish
- [ ] UI: hold-to-record button, recording timer, cancel gesture

### Step 6: Receive and display voice messages
- [ ] Detect MessageType.Audio from imeta m=audio/opus (or audio/*)
- [ ] Audio player widget in message bubble: play/pause button, progress bar, duration
- [ ] On play: MIP-04 download → decrypt → Opus decode → play
- [ ] Cache decoded audio in memory (like image cache)

### Step 7: Desktop UI (Avalonia)
- [ ] Record button next to message input (microphone icon)
- [ ] Recording state: red dot + timer + cancel/send buttons
- [ ] Audio player in MessageBubble: play/pause, seekbar, duration text
- [ ] Upload progress indicator

### Step 8: Android UI
- [ ] Record button in ChatFragment
- [ ] RECORD_AUDIO runtime permission request
- [ ] Recording state UI
- [ ] Audio player in message adapter
- [ ] Upload progress

## Packages
- Concentus (2.2.2) — pure .NET Opus encoder/decoder
- Concentus.OggFile — OGG container read/write

## Blossom Upload Protocol
- HTTP PUT to `{server}/upload`
- Body: raw encrypted bytes
- Header: `Authorization: Nostr <base64 kind 24242 event>`
- Kind 24242 event: tags [["t","upload"], ["x","<sha256>"], ["expiration","<unix>"]]
- Response: JSON `{"sha256":"...","url":"...","size":...}`

## Settings
- Blossom server URL (default: https://blossom.nostr.build)
- Configurable like relays (add/remove in Settings)

## Status
- [ ] Not started
