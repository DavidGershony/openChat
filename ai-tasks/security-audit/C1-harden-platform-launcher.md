# C1 - Harden IPlatformLauncher Against Command Injection

**Severity**: CRITICAL (defense-in-depth)
**Status**: COMPLETED

## Context

`AvaloniaLauncher.OpenUrl()` passes its argument directly to `Process.Start` with `UseShellExecute=true`. On Windows this is shell-interpreted -- a crafted string could execute arbitrary commands.

**Current call sites** (as of audit):
- `SettingsViewModel.OpenLogFolderCommand` -> `OpenFolder(logDir)` (controlled path, low risk)
- QR codes only encode npub or nostrconnect URIs (not passed to OpenUrl currently)

The risk is low *today* because no untrusted strings reach these methods, but any future caller passing relay-sourced data (message links, profile URLs) would create RCE.

## Files
- `src/OpenChat.UI/Services/AvaloniaLauncher.cs` (lines 8-28)
- `src/OpenChat.Presentation/Services/IPlatformLauncher.cs`
- `src/OpenChat.Android/Services/AndroidLauncher.cs` (Android impl for parity)

## Tasks
- [ ] In `OpenUrl`: validate the URL has an `https://` scheme before launching. Reject all other schemes.
- [ ] In `OpenFolder`: validate the resolved path stays within known app directories (e.g. ProfileConfiguration.DataDirectory or LogDirectory). Use `Path.GetFullPath()` and check prefix.
- [ ] Add unit tests: valid https URL passes, `cmd.exe`, `file://`, `javascript:`, empty string all rejected.
- [ ] Add unit tests: valid log folder passes, path traversal (`../../`) rejected.
