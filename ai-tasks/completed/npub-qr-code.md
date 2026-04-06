# Task: Add npub QR code display on Desktop and Mobile

## Status: COMPLETED

## Problem

Users need an easy way to share their npub with others. Currently there is no visual QR code for the user's own public key.

## Changes

### ViewModel (shared)
- `MainViewModel.MyNpubQrPngBytes` — new `[Reactive]` property for QR PNG bytes
- Generated via existing `IQrCodeGenerator.GeneratePng()` when profile dialog opens
- Cleared when dialog closes

### Desktop (Avalonia)
- Added QR code image to My Profile dialog overlay in `MainWindow.axaml`
- Uses `PngBytesToBitmapConverter` (already existed) to bind PNG bytes to Image control
- 200x200 white-background bordered QR above the npub text

### Android
- Added QR `ImageView` to `dialog_my_profile.xml`
- Bound in `ChatListFragment.ShowMyProfileDialog()` via `BitmapFactory.DecodeByteArray`
- 200x200dp white-background QR above the npub label
