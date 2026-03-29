# Linux Secure Storage: Optional Passphrase Protection

## Status: Pending

## Context
Linux secure storage uses AES-256-GCM with a key file at `~/.config/OpenChat/storage.key` (chmod 600). The key is stored in plain text, protected only by filesystem permissions. Windows (DPAPI) and Android (Keystore) have OS-level protection so this is Linux-only.

## Goal
Allow Linux users to optionally encrypt the key file with a passphrase for stronger protection.

## Design

### Key file format
- **Unprotected** (current): raw 32-byte key
- **Protected**: header marker + salt + PBKDF2-derived-key-encrypted AES key

### Startup flow (Linux only)
- Detect if key file is passphrase-protected (by header marker)
- If yes: show unlock dialog (password field + unlock button) before auto-login
- If no: proceed as today (transparent, no prompt)

### Settings (Linux only)
- "Set passphrase" / "Remove passphrase" option in Settings
- Hidden on Windows/Android (not needed)
- Changing passphrase: decrypt key with old passphrase, re-encrypt with new one

### UI
- Unlock dialog: simple Avalonia window with password field + Unlock button
- Settings: button in Developer or Privacy section, only visible on Linux

## Steps
- [ ] Add passphrase encryption/decryption to LinuxSecureStorage (PBKDF2 + AES-256-GCM wrapping the key)
- [ ] Add `IsPassphraseProtected()` and `SetPassphrase(string?)` methods
- [ ] Create Avalonia unlock dialog (shown before ShellViewModel on Linux if needed)
- [ ] Add "Set passphrase" / "Remove passphrase" to SettingsViewModel (Linux-only flag)
- [ ] Add UI in SettingsView.axaml (desktop) — hidden on non-Linux
- [ ] Write tests for passphrase protect/unprotect round-trip
