# Android Feature Parity with Avalonia Desktop

Bring the Android app up to speed with the Avalonia desktop implementation.

## Tasks

### 1. Audio playback in message bubbles
- [x] Add audio player layout (play/pause button, seekbar, duration) to message item layouts
- [x] Update MessageAdapter to handle audio messages (IsAudio) with playback controls
- [x] Wire ToggleAudioCommand, IsPlayingAudio, AudioProgress, AudioDurationText bindings

### 2. Recording UI feedback in chat
- [x] Add recording indicator (red dot, "Recording" text, duration) to chat fragment layout
- [x] Add cancel recording button
- [x] Bind IsRecording, RecordingDuration, CancelRecordingCommand from ChatViewModel
- [x] Hide message input and show recording UI when recording

### 3. Upload status banner in chat
- [x] Add upload status bar to chat fragment layout
- [x] Bind UploadStatus from ChatViewModel

### 4. File attachment button in chat
- [x] Add attach button to chat input area (visible when MIP-04 enabled)
- [x] Implement Android file picker via ActivityResultLauncher + GetContent contract
- [x] Set ChatViewModel.FilePickerFunc in fragment's OnCreate
- [x] Read file data, name, MIME type from content URI

### 5. About section in settings
- [x] Add About card with app description and version

### 6. Logout button in settings
- [x] Add red Logout button at bottom of settings
- [x] Wire to MainViewModel.LogoutCommand with confirmation dialog

### 7. Theme support in settings
- [x] Add theme selection UI in settings (card with theme picker button)
- [x] Create 5 theme styles: Nostr Purple (default), Midnight Blue, Forest Green, Blood Orange, Monochrome
- [x] ThemeService stores preference in SharedPreferences
- [x] MainActivity applies saved theme on create
- [x] Activity.Recreate() on theme change for immediate effect
