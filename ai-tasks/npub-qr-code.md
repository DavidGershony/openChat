# Task: Add npub QR code display on Desktop and Mobile

## Status: Not Started

## Problem

Users need an easy way to share their npub with others. Currently there is no visual QR code for the user's own public key.

## Goal

### Desktop (Avalonia)
1. When clicking the profile image (top-left), show a popup/dialog containing:
   - The user's npub as a QR code
   - The npub text (copyable)
2. Use the existing `IQrCodeGenerator` service to generate the QR image

### Android
1. Add a "Show QR Code" button in the profile/settings screen
2. When tapped, display a dialog/fragment with:
   - The user's npub as a QR code
   - The npub text (copyable)
3. Use the existing `AndroidQrCodeGenerator` service

### Performance note
Generating QR codes from the npub string is lightweight. Cache the generated QR image in memory so it doesn't regenerate on every view.
