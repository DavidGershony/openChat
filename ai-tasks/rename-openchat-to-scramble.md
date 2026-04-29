# Rename OpenChat → Scramble

## Status: Not Started

## Summary

Rename the entire project from "OpenChat" to "Scramble" across all code, config, branding, and CI/CD. New package ID: `app.scramble.chat` (or similar — must be decided before Android publish).

---

## Phase 1: Core Infrastructure (do first — everything else depends on this)

### 1.1 Solution & Project Files (8 files)
Rename files and update contents:
- `OpenChat.sln` → `Scramble.sln`
- `OpenChat.Desktop.slnf` → `Scramble.Desktop.slnf`
- `OpenChat.sln.DotSettings.user` → `Scramble.sln.DotSettings.user`
- `src/OpenChat.Core/OpenChat.Core.csproj` → `src/Scramble.Core/Scramble.Core.csproj`
- `src/OpenChat.Presentation/OpenChat.Presentation.csproj` → `src/Scramble.Presentation/Scramble.Presentation.csproj`
- `src/OpenChat.UI/OpenChat.UI.csproj` → `src/Scramble.UI/Scramble.UI.csproj`
- `src/OpenChat.Desktop/OpenChat.Desktop.csproj` → `src/Scramble.Desktop/Scramble.Desktop.csproj`
- `src/OpenChat.Android/OpenChat.Android.csproj` → `src/Scramble.Android/Scramble.Android.csproj`

Inside each csproj, update:
- `<RootNamespace>`
- `<InternalsVisibleTo>` references
- `<ProjectReference>` paths

### 1.2 Directory Renames (10 directories)
- `src/OpenChat.Android/` → `src/Scramble.Android/`
- `src/OpenChat.Core/` → `src/Scramble.Core/`
- `src/OpenChat.Desktop/` → `src/Scramble.Desktop/`
- `src/OpenChat.Native/` → `src/Scramble.Native/`
- `src/OpenChat.Presentation/` → `src/Scramble.Presentation/`
- `src/OpenChat.UI/` → `src/Scramble.UI/`
- `tests/OpenChat.Core.Tests/` → `tests/Scramble.Core.Tests/`
- `tests/OpenChat.Diagnostics/` → `tests/Scramble.Diagnostics/`
- `tests/OpenChat.UI.Tests/` → `tests/Scramble.UI.Tests/`
- `.idea/.idea.OpenChat/` → `.idea/.idea.Scramble/`

### 1.3 Test Project Files (3 files)
- `tests/OpenChat.Core.Tests/OpenChat.Core.Tests.csproj` → update
- `tests/OpenChat.Diagnostics/OpenChat.Diagnostics.csproj` → update
- `tests/OpenChat.UI.Tests/OpenChat.UI.Tests.csproj` → update

---

## Phase 2: Namespace Refactoring (bulk — use IDE refactoring tools)

### 2.1 C# Namespaces (~104 files)
Global find-replace: `namespace OpenChat.` → `namespace Scramble.`

Affected namespaces:
- `OpenChat.Core` → `Scramble.Core`
- `OpenChat.Core.Services` → `Scramble.Core.Services`
- `OpenChat.Core.Models` → `Scramble.Core.Models`
- `OpenChat.Core.Crypto` → `Scramble.Core.Crypto`
- `OpenChat.Core.Marmot` → `Scramble.Core.Marmot`
- `OpenChat.Core.Configuration` → `Scramble.Core.Configuration`
- `OpenChat.Core.Logging` → `Scramble.Core.Logging`
- `OpenChat.Core.Audio` → `Scramble.Core.Audio`
- `OpenChat.Presentation.ViewModels` → `Scramble.Presentation.ViewModels`
- `OpenChat.Presentation.Services` → `Scramble.Presentation.Services`
- `OpenChat.UI` → `Scramble.UI`
- `OpenChat.UI.Views` → `Scramble.UI.Views`
- `OpenChat.UI.Controls` → `Scramble.UI.Controls`
- `OpenChat.UI.Converters` → `Scramble.UI.Converters`
- `OpenChat.UI.Services` → `Scramble.UI.Services`
- `OpenChat.Android` → `Scramble.Android`
- `OpenChat.Android.Services` → `Scramble.Android.Services`
- `OpenChat.Android.Fragments` → `Scramble.Android.Fragments`
- `OpenChat.Desktop` → `Scramble.Desktop`
- `OpenChat.Diagnostics` → `Scramble.Diagnostics`

### 2.2 Using Statements (~141 files)
Global find-replace: `using OpenChat.` → `using Scramble.`

---

## Phase 3: Native Library (Rust)

### 3.1 Cargo.toml
`src/OpenChat.Native/Cargo.toml`:
- `name = "openchat_native"` → `scramble_native`
- `[lib] name = "openchat_native"` → `scramble_native`
- Update description

### 3.2 P/Invoke Library Name
`src/OpenChat.Core/Marmot/MarmotInterop.cs`:
- `private const string LibraryName = "openchat_native"` → `"scramble_native"`
- `private const string LibraryName = "libopenchat_native"` → `"libscramble_native"` (Android)

### 3.3 Build Artifacts
All CI/CD and copy targets referencing:
- `openchat_native.dll` → `scramble_native.dll`
- `libopenchat_native.so` → `libscramble_native.so`
- `libopenchat_native.dylib` → `libscramble_native.dylib`

---

## Phase 4: Android Specifics

### 4.1 Package ID (CRITICAL — cannot change after Play Store publish)
`src/OpenChat.Android/OpenChat.Android.csproj`:
- `<ApplicationId>com.openchat.app</ApplicationId>` → `app.scramble.chat` (or chosen domain)

### 4.2 Manifest
`src/OpenChat.Android/Properties/AndroidManifest.xml`:
- `android:label="OpenChat"` → `android:label="Scramble"`

### 4.3 Keystore
- `openchat-release.keystore` → `scramble-release.keystore` (or generate new)
- Update alias in signing config
- Update `ANDROID-SIGNING.md`

### 4.4 Android Resources
String references in layout XML files and fragments that display "OpenChat".

---

## Phase 5: Desktop / Avalonia UI

### 5.1 Window Titles
- `src/OpenChat.Desktop/App.axaml.cs` line 106: `"OpenChat"` → `"Scramble"`
- `src/OpenChat.UI/Views/MainWindow.axaml`: `Title="OpenChat"` → `Title="Scramble"`

### 5.2 XAML Namespace URIs (11 files)
All `avares://OpenChat.UI/` URIs → `avares://Scramble.UI/`
- Theme references in App.axaml
- Resource dictionaries
- Custom controls

### 5.3 Login View
- `src/OpenChat.UI/Views/LoginView.axaml`: Header text "OpenChat" → "Scramble"

---

## Phase 6: Branding Strings (~15 locations)

User-visible strings in code:
- `MainActivity.cs` — log labels, notification channel name
- `ShareTargetActivity.cs` — share target label
- `RelayForegroundService.cs` — notification text
- `ProfileConfiguration.cs` — default profile name
- `LoggingConfiguration.cs` — log property
- `StorageService.cs` — data directory name: `"OpenChat"` → `"Scramble"`
- `MessageService.cs` — cache directory name
- `LinuxSecureStorage.cs` — config directory path
- `MainViewModel.cs` — header display name
- `ChatListViewModel.cs` — status messages

**Important:** Storage/data directory rename needs migration logic or the app loses existing data on update. Consider keeping old path as fallback.

---

## Phase 7: CI/CD (2 workflow files)

### 7.1 `.github/workflows/dotnet-desktop.yml`
- Path references to `src/OpenChat.Native`, `OpenChat.Desktop.slnf`
- Native DLL copy paths

### 7.2 `.github/workflows/publish.yml`
- All project path references
- Native library names (dll/so/dylib)
- Artifact names: `OpenChat-*.exe` → `Scramble-*.exe`, `OpenChat-*.apk` → `Scramble-*.apk`
- Keystore references

---

## Phase 8: Documentation

- `README.md` — title, all references
- `CLAUDE.md` — project description table, all references
- `ANDROID-SIGNING.md` — keystore references
- `ai-tasks/*.md` — references in task files (low priority)

---

## Decisions Needed Before Starting

1. **Package ID:** `app.scramble.chat`? `io.scramble.app`? Must decide before Android publish.
2. **Domain:** Register `scramble.app` or `scramble.chat` for privacy policy URL.
3. **Data migration:** Should existing desktop/mobile installs migrate the `OpenChat` data directory to `Scramble`, or keep backward compatibility?
4. **New keystore:** Generate a fresh `scramble-release.keystore` or rename existing?
5. **Git repo name:** Rename `openChat` repo to `scramble`?

---

## Estimated Effort

| Phase | Effort | Risk |
|-------|--------|------|
| 1. Solution/project files | 1-2 hours | High (breaks build if wrong) |
| 2. Namespace refactoring | 1 hour (IDE tool) | Medium (merge conflicts) |
| 3. Native library | 30 min | Medium (cross-platform build) |
| 4. Android specifics | 1 hour | High (package ID is permanent) |
| 5. Desktop UI | 30 min | Low |
| 6. Branding strings | 1 hour | Medium (data dir migration) |
| 7. CI/CD | 1 hour | Medium |
| 8. Documentation | 30 min | Low |
| **Total** | **~6-8 hours** | |

**Recommendation:** Do this in a single dedicated session on a clean branch. Don't interleave with feature work. Run full test suite after each phase.
