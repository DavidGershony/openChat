# Audio & Video Calls

## Status: Not Started (Analysis Complete)

## Summary

Add real-time audio/video calling to OpenChat using WebRTC for media transport and MLS for signaling/key exchange. SDP offers/answers and ICE candidates are exchanged as MLS-encrypted application messages, giving E2EE media without reinventing transport.

## Architecture

- **Signaling**: MLS group messages carry SDP offers/answers + ICE candidates
- **Media**: WebRTC handles actual audio/video with DTLS-SRTP
- **E2EE**: Keys derived from (or authenticated by) the MLS exporter secret

## Three Paths Evaluated

### Path A: AAR on Android + SIPSorcery on Desktop
- **Pros**: Fastest to working
- **Cons**: Two different WebRTC stacks, weakest desktop video, SIPSorcery has no AEC/NS/AGC (speakerphone unusable), no built-in video codecs, basic jitter buffer (choppy audio on bad networks)

### Path B: Fork SIPSorcery + Bind Stable C Libraries (Recommended)
- **Pros**: Best control, stable APIs, low security churn, single cross-platform codebase
- **Cons**: More upfront work than Path A
- **Libraries to bind** (all BSD licensed, stable APIs):
  - `libvpx` — VP8/VP9 video codec (Google)
  - `openh264` — H.264 codec (Cisco)
  - `webrtc-audio-processing` — AEC3 + NS + AGC, standalone C library extracted from libwebrtc
  - `opus` — audio codec (native libopus faster than managed Concentus)
  - `dav1d` / `libaom` — AV1 codec (optional)
- **C# code to write** (protocol + glue):
  - Jitter buffer / NetEQ-equivalent (port from libwebrtc source, BSD)
  - RTP extensions (transport-cc, simulcast signaling, FEC framing)
  - Bandwidth estimation (GCC algorithm)
  - Integration with SIPSorcery's existing `RTCPeerConnection`
- **Platform glue** (per-OS):
  - Camera: Camera2 (Android), MediaFoundation (Windows), AVFoundation (macOS), V4L2 (Linux)
  - Audio: AudioRecord/AudioTrack (Android), WASAPI (Windows), CoreAudio (macOS), ALSA/PulseAudio (Linux)
  - Hardware codecs: MediaCodec (Android), VideoToolbox (macOS), D3D11VA/MediaFoundation (Windows)

### Path C: Bind Full libwebrtc (via `webrtc-sdk/libwebrtc` prebuilts)
- **Pros**: Strongest quality, battle-tested, prebuilt binaries available (~200MB per platform, not 30GB Chromium checkout)
- **Cons**: Frequent CVEs in network parsers mean you CANNOT pin a version — must track upstream security fixes 3-6x/year. API churn between versions. Community-maintained prebuilts (`webrtc-sdk/libwebrtc` powers flutter-webrtc/LiveKit).
- **Prebuilt sources**: `webrtc-sdk/libwebrtc` (primary), `crow-misia/libwebrtc-bin`, `sourcey/webrtc-builds`

### Hybrid Option for Android
- Bind `stream-webrtc-android` AAR (or `io.getstream:stream-webrtc-android`) via .NET Android Binding Library
- Gets real AEC, hardware H.264/VP8 decode, battery-friendly — what Signal/Element/Matrix Android do
- Effort: 2-5 days for binding, then stable
- Desktop still needs separate solution (SIPSorcery fork or libwebrtc)

## Estimated Timeline (With AI)

| Goal | What it gives the app | Pre-AI | With AI |
|---|---|---|---|
| Fork SIPSorcery, add VP8/H.264 via libvpx/openh264 bindings, basic 1:1 video | **You can see each other.** Video codecs compress camera frames to fit through internet. Without this, video is impossible. | 6-12 months | **3-6 weeks** |
| Add AEC3/NS/AGC by binding `webrtc-audio-processing` | **Calls don't sound terrible on speaker.** Echo cancellation, noise suppression, auto gain. Without these, speakerphone is unusable. | 3-6 months | **1-3 weeks** |
| Port NetEQ-class jitter buffer | **Calls survive bad wifi/mobile data.** Smooths out packet loss and reordering. Without it, works on fiber but falls apart on 4G. | 1-2 years | **2-4 months** (tuning dominates) |
| Full parity: simulcast, transport-cc, SVC, HW codecs, polish | **Group calls work, phone doesn't overheat.** Multiple quality levels, real-time bandwidth adjustment, dedicated video chip usage. "Feels like a real product" vs "tech demo." | 3-5 years | **6-12 months** |

Ship usefully after row 2 (1:1 calls with headphones). Row 3 = "recommend to a friend." Row 4 = "competes with Signal."

## Security Considerations

- **Cannot pin full libwebrtc** — frequent CVEs in network parsers (RTP, SDP, DTLS, SCTP). Pinning = shipping known-exploitable code.
- **Can mostly pin standalone codec libs** (libvpx, openh264, opus) — narrow attack surface, process media from already-authenticated peers, rare CVEs.
- `webrtc-audio-processing` — very rare CVEs, safe to pin.
- This security asymmetry is the strongest argument for Path B (bind stable C libs) over Path C (bind full libwebrtc).

## Microsoft.MixedReality.WebRTC (Reference Only)

- MIT licensed, archived since 2020 at `microsoft/MixedReality-WebRTC`
- Contains a C++ wrapper (`mrwebrtc`) that flattens libwebrtc's C++ API into a C ABI, plus C# bindings on top
- Useful as a **design reference** for how to wrap libwebrtc (naming, object lifetimes, callback marshalling, threading)
- NOT viable as a dependency — frozen against 2019 libwebrtc (M71), 7 years of security fixes missing

## Work Breakdown (Path B)

- ~20% C# binding code (P/Invoke to 3-4 stable C libraries)
- ~40% C# protocol/algorithm code (ported NetEQ, RTP extensions, BWE — highest AI leverage)
- ~30% C# platform glue (camera, audio, HW codec per OS — AI-friendly, well-documented APIs)
- ~10% tuning and real-world testing (least AI leverage)

## Analysis Date

April 15, 2026 — full conversation in Claude Code session `66d10846-9777-4ebc-809b-943041755412`
