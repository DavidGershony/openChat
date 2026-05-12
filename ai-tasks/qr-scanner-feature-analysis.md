# QR Scanner for New Chat — Architecture Analysis

**Status:** Investigation complete, awaiting decision before any code changes.
**Branch:** `feature/qr-scan-newchat` (off master `2044d75`, no commits yet).
**Date:** 2026-05-10.

---

## Background

Goal: let users scan a QR code (npub / nprofile / `nostr:` URI) on the New Chat screen instead of pasting. Initially scoped to **Android only** (camera scanning is meaningless on desktop; desktop keeps paste). Per `CLAUDE.md`, UI features normally land in both `Scramble.Android` *and* `Scramble.UI` — this is a deliberate exception.

User flagged before any code was written: *"consider Avalonia 12 is out for mobile support and perhaps we will just unify the app so before starting the work we need to investigate"*. So this doc evaluates **two questions in one go**:

1. **Tactical:** which library + UX pattern for the QR scanner itself?
2. **Strategic:** should we unify on Avalonia 12 mobile and delete `Scramble.Android` instead of growing it?

---

## How other Nostr clients do it

| Client | Stack | Library | Entry points | Payload coverage |
|---|---|---|---|---|
| **Damus** (iOS) | SwiftUI | `twostraws/CodeScanner` (AVFoundation wrapper) | NSEC import + NWC pairing only — no global "add contact via QR" | Per-screen validation, no unified router |
| **Amethyst** (Android) | Compose | `journeyapps/zxing-android-embedded` (~60 LOC) | One screen toggles "show my QR ↔ scan QR" | `uriToRoute()` handles `nostr:` URIs + bech32 entities |
| **Primal Android** | Compose | **CameraX + per-flavor decoder** (MLKit for Play, ZXing for AOSP/F-Droid), `LocalQrCodeDecoder` composition local | Multiple, including Add Contact and a "scan anything" surface | Richest in the wild: `NPUB[_URI]`, `NPROFILE[_URI]`, `NADDR[_URI]`, `NEVENT[_URI]`, `NOTE[_URI]`, `LIGHTNING_URI`, `LNBC`, `LNURL`, `BITCOIN_URI`, `BITCOIN_ADDRESS`, `NWC_URL`, `NOSTR_CONNECT`, `PROMO_CODE` |
| **Primal iOS** | UIKit | Raw `AVFoundation` (`AVCaptureSession` + `AVCaptureMetadataOutput`) | Profile-scan + "scan anything" via `DeeplinkCoordinator.canHandleURL` | Same model as Android |
| **0xchat** (Flutter) | Flutter | `mobile_scanner` 6.x | Add friend, group invite, NIP-46 remote-signer login (generates `nostrconnect://`) | Single `ScanUtils.analysis()` dispatcher |
| **Web clients** (Coracle/Snort/Iris) | Browser | Browser `BarcodeDetector` API + JS fallback | Mostly *display* QR; scanning rare on desktop | n/a here |

### Patterns worth stealing

1. **Single screen, "show my QR ↔ scan a QR" toggle** (Amethyst, 0xchat).
2. **"Enter manually / Use Keyboard Instead"** affordance under the viewfinder (Primal iOS).
3. **Permission-denied → deep link to system settings** (Primal iOS; on Android use `Settings.ACTION_APPLICATION_DETAILS_SETTINGS`).
4. **Debounce identical decodes by ~2 s** (Damus + Primal both do this; without it the analyzer fires 30×/sec).
5. **Single payload-type enum + one dispatcher** (Primal's `QrCodeDataType` + 0xchat's `ScanUtils.analysis`). Even if v1 only handles npub/nprofile, having the enum makes nevent/lnbc/nwc trivial follow-ups.

### Anti-patterns

- **Don't use `journeyapps/zxing-android-embedded`** — it launches its own Activity, breaks UI continuity, dated look. Acceptable as a same-day MVP but you'll regret it.
- **Don't use ZXing.Net.Mobile** — dead since Xamarin EOL.
- **Don't use ZXing.Net.MAUI** — MAUI handlers don't run in Avalonia or in a Views project.

---

## Android library landscape (2024–2026)

| Approach | Pros | Cons | Verdict |
|---|---|---|---|
| **CameraX + MLKit BarcodeScanning** | Best decode quality (angle/low-light), Google-maintained, on-device | Adds ~10 MB Play Services dep, breaks on de-Googled phones / GrapheneOS / F-Droid | Default for Play store flavor |
| **CameraX + ZXing.Net** | No Play Services, fully OSS, small | Slower, you write the YUV→`LuminanceSource` analyzer (~50 LOC) | Default for F-Droid / GMS-free flavor |
| **Bare Camera2** | Maximum control | Don't. CameraX exists for a reason. | Skip |
| **`zxing-android-embedded`** | Drop-in, ~50 LOC | Own Activity, can't theme, dated | MVP only |

**.NET for Android NuGets** (all multi-targeting `net9.0-android`):
- `Xamarin.AndroidX.Camera.Core`, `Camera.Camera2`, `Camera.Lifecycle`, `Camera.View`
- `Xamarin.GooglePlayServices.MLKit.BarcodeScanning` (Play flavor)
- `ZXing.Net 0.16.11` (GMS-free flavor)

**Primal's flavor-swap pattern** is the cleanest reference: an `IQrCodeResultDecoder` interface injected at composition time, swappable per build flavor. Worth copying even if we ship one flavor today.

---

## Avalonia 12 mobile — current state (May 2026)

**Headline:** Avalonia 12.0 (released 7 Apr 2026, latest 12.0.2 on 28 Apr) is officially marketed as **production-ready on mobile**, not preview. Real measurable wins:

- 3× general perf improvements
- NativeAOT startup **1,960 ms → 460 ms** (~4×)
- Scrolling **42 → 120 FPS**
- Idle CPU **0.20% → <0.01%**
- Proper `Looper`/`MessageQueue`-based dispatcher
- `IActivityApplicationLifetime` for Android lifecycle
- In-box page navigation (`ContentPage`, `DrawerPage`, `CarouselPage`, `TabbedPage`)

### What works on Android

| Feature | Status |
|---|---|
| Page navigation / hardware back | ✅ |
| Lifecycle hooks | ✅ via `IActivityApplicationLifetime` |
| Config changes / rotation / safe-area | ✅ (rewritten in 12.0) |
| `IStorageProvider` (SAF) | ✅ (caveat: no `content://` URL parsing — bug #19640) |
| `NativeControlHost` (embed Android `View`) | ✅ documented |
| 16 KB page size (Android 15+) | ✅ |
| .NET 9 / `net9.0-android` | ⚠️ Avalonia 12 itself targets .NET 10; works on .NET 9, but recommended runtime moved on |

### What's missing or broken

| Feature | Status |
|---|---|
| **Camera control / preview** | ❌ Open enhancement #12956 since Sept 2023, still unresolved |
| **Notifications API** | ❌ No cross-platform abstraction — drop to `NotificationManager` directly |
| **Foreground services / WorkManager** | ❌ Pure Android-side; not Avalonia's concern (good for us — we keep `RelayForegroundService.cs` verbatim) |
| **Material 3 theming** | ❌ Skia-rendered Fluent/Simple themes only; no native Material widgets |
| **`ActivityResultContracts`** | ❌ No abstraction — bring your own |
| **`ShareTargetActivity`** abstraction | ❌ Not provided |
| **Soft keyboard / IME** | ⚠️ Multiple open bugs: #20232 (TextBox+wrap "extremely buggy", Dec 2025), #20852 (cursor across paragraphs, Mar 2026), #16991, #17166 |
| **Edge-to-edge** | ⚠️ Bug #18544 — can't transparent system bars |
| **Glyph/font rendering on Android** | ⚠️ Open bugs #20195, #19843, #18664 (emoji invisible on Android 15) |
| **Avalonia QR scanner library** | ❌ Only `Plugin.Scanner.Avalonia` v0.0.1 (Mar 2026, **208 total downloads**, single author, alpha) |
| **Avalonia camera library** | ❌ None — must hand-roll via `NativeControlHost` + CameraX |

### Critical constraint: `NativeControlHost` z-order

From the official docs: an embedded native view *"always renders on top of Avalonia content"*, **cannot be transparent**, **cannot be transformed**. So you can embed a CameraX `PreviewView` inside Avalonia, but you **cannot draw an Avalonia scan reticle / instructions on top of it**. Workarounds: draw the overlay inside the native `FrameLayout`, or inset the preview and put Avalonia chrome around (not over) it.

For a QR scanner with a viewfinder rectangle and dim mask, this is a real UX cut.

### Migration model

Avalonia takes over `MainActivity` (or you subclass `AvaloniaActivity`). There is **no equivalent of Compose's `ComposeView`** — you cannot host an Avalonia control inside an existing AndroidX `Fragment`. Incremental migration means launching `AvaloniaActivity` as a sibling activity via Intent. **It is effectively all-or-nothing for the main UI**.

---

## What porting `Scramble.Android` to Avalonia 12 would cost

Inventory of `src/Scramble.Android` (64 tracked files, 24 C# files, **5,339 LOC**):

| Bucket | LOC | % | Fate |
|---|---|---|---|
| **Dies** — 7 Fragments + 7 Adapters + ToolbarListeners + ThemeService + all 21 layouts/menus | ~3,734 | **~70%** | Replaced by reusing `Scramble.UI` Avalonia views against the same `Scramble.Presentation` ViewModels |
| **Ports as platform glue** — parts of MainActivity (lifecycle/intent/signer-reconnect), ShareTargetActivity, ChatFragment file-picker | ~700 | ~13% | Lifts into Avalonia.Android head, trims down |
| **Stays verbatim** — Keystore, Audio rec/playback, Notification, ForegroundService, FileProvider, Clipboard, Launcher, QrCodeGenerator | ~775 | ~15% | Lifts into Avalonia.Android head; QrCodeGenerator could promote to `Scramble.Core` |

Plus dead packages: `ReactiveUI.AndroidX`, `Xamarin.Google.Android.Material`, `Xamarin.AndroidX.Lifecycle.LiveData.Core`.

### Wins from migrating

- **Eliminates 97 hand-written `WhenAnyValue` binding chains** across Fragments — all replaced by Avalonia XAML `{Binding}` already present in `Scramble.UI`.
- **Deletes the `CLAUDE.md` "two UI targets must stay in sync" maintenance burden.**
- **Single design system** (Fluent/Simple) across desktop and Android.
- 70% of `Scramble.Android` evaporates.

### Costs from migrating

- **Camera scanner**: still a hand-rolled `NativeControlHost` + CameraX + MLKit/ZXing job — Avalonia doesn't help here. **And we lose the ability to overlay an Avalonia reticle on the preview** (z-order constraint).
- **IME stability**: 4 open Android text-input bugs in late-2025/early-2026. Our app is a chat app — TextBox is the most-used control. This is the single biggest risk.
- **Material 3 look**: Android users expect Material; Skia-Fluent will look foreign.
- **Notifications, ShareTargetActivity, NIP-46 signer flow, file picker, foreground service** — none abstracted by Avalonia. We re-do the wiring in Avalonia idiom.
- **All-or-nothing migration** for the main UI (`AvaloniaActivity` owns the activity).
- **.NET 10 recommended** — Avalonia 12's own targets moved on; we'd want to bump.
- **Migration scope:** ~3,700 LOC rewrite + ~700 LOC port + retest the entire Android app (chat, settings, login, share-target, voice, file picker, foreground relay, NIP-46 signer round-trip).
- We've spent multiple sprints stabilising the current Android shell (see `ai-tasks/completed/android-feature-parity.md`, `android-production-ready.md`, `android-google-play-readiness.md`). Throwing it away costs the regression risk on all of that.

---

## Recommendation

### Strategic question: **don't migrate to Avalonia mobile yet.**

Avalonia 12 is genuinely impressive and the long-term direction is probably right. But for **this app, this quarter**:

1. **The features Scramble depends on most are exactly the ones Avalonia doesn't abstract**: camera, notifications, foreground service, NIP-46 ActivityResult, share-target, Material theming.
2. **IME bugs are an existential risk for a chat app** — 4 open issues in the last 6 months on the most-used control.
3. **The QR scanner specifically is *harder* under Avalonia**, not easier — same CameraX work plus the z-order overlay constraint.
4. **All-or-nothing migration** means a multi-sprint rewrite with regression risk on a recently-stabilised app.
5. **Revisit in 12–18 months** once #12956 (camera), notifications API, and IME stability land. If we want to start preparing now, the right move is to keep extracting more logic from `Scramble.Android` into `Scramble.Core` / `Scramble.Presentation` so a future port is smaller — but that's an orthogonal cleanup, not this task.

### Tactical: ship the Android-native QR scanner now, copy Primal's architecture

**Library choice:** `Xamarin.AndroidX.Camera.*` (CameraX) + `Xamarin.GooglePlayServices.MLKit.BarcodeScanning`.
- Justification: best decode quality, ~50 LOC of glue. We're shipping to Play Store first; F-Droid/GrapheneOS users are a follow-up.
- Hide the decoder behind an `IQrCodeResultDecoder` interface (Primal pattern) so we can add a ZXing flavor later for an F-Droid build without touching the camera plumbing.

**Permission model:**
- Add `<uses-permission android:name="android.permission.CAMERA" />` and `<uses-feature android:name="android.hardware.camera" android:required="false" />` to `AndroidManifest.xml`.
- Runtime request via existing `ActivityCompat.RequestPermissions` pattern (copy from `AndroidAudioRecordingService`'s `RECORD_AUDIO` flow at `src/Scramble.Android/Services/AndroidAudioService.cs`).
- Permission-denied path: alert with a "Open Settings" button → `Settings.ActionApplicationDetailsSettings` intent.

**UX placement (per earlier decision, unchanged by this analysis):**
- Add an end-icon to the existing `TextInputLayout` `@+id/new_chat_participant_layout` in `fragment_new_chat.xml` (`app:endIconMode="custom"`, `app:endIconDrawable="@drawable/ic_qr_scan"`).
- Wire `participantLayout.SetEndIconOnClickListener(...)` in `NewChatFragment.OnViewCreated`.
- Tap → launch `ScanQrActivity` via `StartActivityForResult` → on result, set `ViewModel.NewChatParticipantInput` (live-validation pipeline at `ChatListViewModel.cs:288-316` does the rest).

**Payload coverage v1:**
- Plain `npub1...`
- `nostr:npub...` (strip prefix, decode)
- `nostr:nprofile...` (NIP-19 TLV decode → extract pubkey type 0; capture relay hints type 1 for follow-up "use these relays" UX)
- 64-char hex pubkey
- Bare `nprofile1...` (uncommon but trivial)

**Out of scope v1:** nevent, naddr, note, lightning, lnurl, bitcoin, NWC, nostrconnect. But add the `QrCodeDataType` enum now so adding them later is one switch case each.

**Code touchpoints:**
1. New `src/Scramble.Core/Crypto/Nip19.cs` — TLV parser on top of existing `Bech32.Decode`. Decode npub/nprofile, return `(byte[] pubkey, IReadOnlyList<string> relayHints)`. Exported types and helpers shared with desktop.
2. Extend `ChatListViewModel.TryAddChatParticipant` (`src/Scramble.Presentation/ViewModels/ChatListViewModel.cs:922-985`) and the live-validation pipeline at `:288-316` to recognise nprofile + `nostr:` URI prefix. Both paths route through `Nip19`.
3. Optional: also extend `ChatViewModel.cs:741` (existing-chat invite) for consistency.
4. `src/Scramble.Android/Properties/AndroidManifest.xml` — `CAMERA` permission + camera feature.
5. `src/Scramble.Android/Scramble.Android.csproj` — add CameraX + MLKit Barcode `PackageReference`s.
6. New `src/Scramble.Android/Activities/ScanQrActivity.cs` + `Resources/layout/activity_scan_qr.xml` + `Resources/drawable/ic_qr_scan.xml`.
7. New `src/Scramble.Android/Services/IQrCodeResultDecoder.cs` + `MlKitQrCodeDecoder.cs`.
8. `src/Scramble.Android/Resources/layout/fragment_new_chat.xml` — add end-icon attrs to `TextInputLayout`.
9. `src/Scramble.Android/Fragments/NewChatFragment.cs` — wire `SetEndIconOnClickListener`, launch `ScanQrActivity`, handle `OnActivityResult`.

**Effort estimate:** ~1 day for the scanner (Activity + camera + MLKit), ~half day for `Nip19.cs` + ViewModel extension + tests, ~half day for end-icon UI + permission flow + manual test. Plus desktop already won't get it — paste-only stays.

### Side-task surfaced during research

User flagged separately: *"we should have nprofile actually"* — our **outgoing** QR code at `src/Scramble.Android/Services/AndroidQrCodeGenerator.cs` currently emits plain `npub`. Once `Nip19.cs` lands, encode `nprofile1...` with the user's preferred relays as type-1 hints. **Tracked separately**, not in this task's scope.

---

## Decision needed

Pick one:

**A. Ship Android-native QR scanner now (recommended).**
- CameraX + MLKit, Primal architecture, decoder interface for future ZXing flavor.
- Avalonia migration deferred 12–18 months.
- Code changes scoped to ~9 files listed above. ~2 days of work.

**B. Spike Avalonia 12 mobile first** (delays QR by ~1 sprint).
- Build a throwaway `Plugin.Scanner.Avalonia` v0.0.1 spike to gauge real maturity of camera+overlay on Android.
- Decide on full migration based on the spike before writing any production code.
- Risk: spike consumes a sprint and we still ship the same Android-native scanner anyway.

**C. Migrate `Scramble.Android` → Avalonia 12 mobile now** (NOT recommended).
- ~3,700 LOC dies, ~700 LOC ports, full Android regression test cycle.
- QR scanner still hand-rolled; overlay constraint hurts UX.
- IME bugs are an existential risk for a chat app.
- Multi-sprint commitment, throws away recently stabilised work.

**Recommendation: A.** Revisit Avalonia mobile as a separate exploratory task in Q4 2026 / Q1 2027.
