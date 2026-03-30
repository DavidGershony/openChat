# Task: Publish Avalonia UI as Android APK

## Status: Not Started

## Problem

The publish workflow produces an Android APK from `OpenChat.Android` (native AndroidX/Material UI) but not from the Avalonia UI layer. There is no way to compare the Avalonia UI experience on Android side-by-side with the native Android app.

## Goal

Add a second Android APK artifact to the publish workflow that builds the Avalonia UI targeting Android via `Avalonia.Android`, published alongside the existing native Android APK.

## What's Needed

### 1. Multi-target `OpenChat.UI` to `net9.0;net9.0-android`

- `NAudio` is Windows-only — must be conditionally included (desktop only)
- `System.Security.Cryptography.ProtectedData` — desktop only
- `DesktopAudioService.cs` and `DesktopSecureStorage.cs` need `#if !ANDROID` or platform-conditional compilation

### 2. New project: `src/OpenChat.Avalonia.Android/`

- Target: `net9.0-android` (API 24+)
- Packages: `Avalonia.Android`, `Avalonia.Themes.Fluent`, `Avalonia.ReactiveUI`
- References: `OpenChat.Core`, `OpenChat.Presentation`, `OpenChat.UI`
- `MainActivity` hosts Avalonia via `AppBuilder.Configure<App>().UseAndroid()`
- Reuses existing `App.axaml` and all views from `OpenChat.UI`
- ApplicationId: `com.openchat.avalonia` (distinct from native `com.openchat.app`)

### 3. Platform services for Avalonia Android

- Audio: stub or Android-specific implementations (NAudio won't work)
- SecureStorage: Android KeyStore-based implementation or reuse from `OpenChat.Android`
- Clipboard, QR, Launcher: Avalonia's built-in APIs should work cross-platform

### 4. Add to solution and publish workflow

- Add project to `OpenChat.sln`
- Add `build-avalonia-android` job to `.github/workflows/publish.yml`
- Artifact name: `OpenChat-Avalonia.apk` (to distinguish from `OpenChat.apk`)
- Uses same signing keystore as the native Android build

## Notes

- The primary purpose is comparison — seeing how the Avalonia UI looks/feels on Android vs the native Android UI
- The existing `OpenChat.Desktop.slnf` solution filter should exclude this project (desktop CI only)
