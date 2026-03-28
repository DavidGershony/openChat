# Android Feature Parity with Avalonia Desktop

Bring the Android app up to speed with the Avalonia desktop implementation.

## Completed

### 1. Audio playback in message bubbles
- [x] Add audio player layout (play/pause button, seekbar, duration) to message item layouts
- [x] Update MessageAdapter to handle audio messages (IsAudio) with playback controls
- [x] Wire ToggleAudioCommand, IsPlayingAudio, AudioProgress, AudioDurationText bindings

### 2. Recording UI feedback in chat
- [x] Add recording indicator (red dot, "Recording" text, duration) to chat fragment layout
- [x] Add cancel recording button
- [x] Bind IsRecording, RecordingDuration, CancelRecordingCommand from ChatViewModel

### 3. Upload status banner in chat
- [x] Add upload status bar to chat fragment layout
- [x] Bind UploadStatus from ChatViewModel

### 4. File attachment button in chat
- [x] Add attach button to chat input area (visible when MIP-04 enabled)
- [x] Implement Android file picker via ActivityResultLauncher
- [x] Set ChatViewModel.FilePickerFunc

### 5. About section and Logout in settings
- [x] Add About card with app description and version
- [x] Add red Logout button with confirmation dialog

### 6. Theme support (Android)
- [x] 6 themes: Nostr Purple, Midnight Blue, Forest Green, Golden Axe, Blood Orange, Monochrome
- [x] ThemeService with SharedPreferences persistence
- [x] Activity.Recreate() on theme change

### 7. Theme support (Avalonia Desktop)
- [x] Convert all StaticResource to DynamicResource for runtime swapping
- [x] Replace inline hex colors with theme resource references
- [x] Split NostrTheme.axaml into structural Styles + swappable NostrColors.axaml
- [x] Create GoldenAxeTheme.axaml with Golden Axe color palette
- [x] ThemeService in App.axaml.cs swaps MergedDictionaries at runtime
- [x] Theme ComboBox in SettingsView wired to SettingsViewModel
