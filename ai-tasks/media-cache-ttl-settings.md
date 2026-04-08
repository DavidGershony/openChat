# Media Cache TTL — Settings UI

**Status**: TODO
**Priority**: Nice-to-have

## Goal

Add a configurable media cache TTL to the Settings page so users can control how long
decrypted media files are kept on disk before automatic cleanup.

## Requirements

- [ ] Add "Media cache retention" setting to SettingsViewModel with options:
  - 7 days, 30 days (default), 90 days, Forever
- [ ] Add "Max cache size" setting (e.g. 200MB, 500MB (default), 1GB, Unlimited)
- [ ] Persist settings via ProfileConfiguration or a settings table in the DB
- [ ] On app startup, run cleanup: delete cached files older than TTL, then evict
  oldest files if total size exceeds the limit
- [ ] Add "Clear media cache now" button to Settings (with confirmation)
- [ ] Show current cache size in Settings (e.g. "Media cache: 142 MB, 38 files")
- [ ] Log cleanup stats at Info level on startup

## Files
- `src/OpenChat.Core/Services/MediaCacheService.cs` — add cleanup methods
- `src/OpenChat.Presentation/ViewModels/SettingsViewModel.cs` — UI bindings
- `src/OpenChat.UI/Views/SettingsView.axaml` — settings controls
- `src/OpenChat.Core/Configuration/ProfileConfiguration.cs` — persist settings
