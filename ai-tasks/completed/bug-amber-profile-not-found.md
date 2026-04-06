# Bug: Desktop Amber always requires new connection (can't find profile)

## Problem
On desktop, when using Amber (external signer), the user always has to start a new
connection as if the app can't find the existing profile. The saved session / profile
is not recognized on subsequent launches.

## Root Cause
`last_user.json` doesn't exist in `%LOCALAPPDATA%/OpenChat-Dev/`. Without it,
`TryAutoLoginAsync()` can't derive the profile name, `IsCustomProfile` stays false,
and the saved-user loading block is skipped entirely.

The auto-login flow IS correct when `last_user.json` exists:
```
Line 82: if (!IsCustomProfile) → ReadLastUserPubKey() → SetProfile(derived) → IsCustomProfile = true
Line 94: if (IsCustomProfile)  → load saved user → ActivateSession → RestoreSessionAsync
```

**All signer credentials ARE persisted** (relay URL, remote pubkey, secret, ephemeral
keypair). The reconnect code in `MainViewModel.InitializeAfterLoginAsync()` via
`ExternalSignerService.RestoreSessionAsync()` exists and is correct. Profile folder
`profiles/e9b03d7d20c787ce/` exists with the DB — the data is all there.

The issue is `WriteLastUserPubKey()` at ShellViewModel.cs:141 — it either failed
silently (the method has a catch-all that swallows all errors) or was never reached.
No `--profile` regression — the `IsCustomProfile` guard works correctly when
`last_user.json` exists.

## Log Evidence

### April 2 restart (broken):
```
18:28:10.532 [INF] MainWindow created successfully (Profile: default)
                                                             ^^^^^^^ Wrong profile!
(No "Auto-login: found saved user" logs — falls through to login screen)
```

### April 6 manual login (works, but requires QR scan):
```
16:36:17.062 [INF] MainWindow created successfully (Profile: default)  ← Still default
16:36:47.309 [INF] Login completed for npub1axcr6lf...                 ← User scanned QR
16:36:47.310 [INF] Profile set to e9b03d7d20c787ce                     ← NOW set correctly
16:36:47.600 [INF] Session activated for npub1axcr6lf...
```

### Missing logs on restart (these never appear):
- `"Auto-derived profile ... from last_user.json"`
- `"Auto-login: found saved user ..."`
- `"Signer session restored successfully"`

## Code Flow

### ShellViewModel.TryAutoLoginAsync() (the broken path):
```
1. ProfileConfiguration.IsCustomProfile = false (startup default)
2. ReadLastUserPubKey() from last_user.json → may return the pubkey
3. SetProfile(derived) → sets profile name
4. if (ProfileConfiguration.IsCustomProfile) → FALSE → skips saved user load
5. ShowLoginView() → user must scan QR again
```

### What SHOULD happen:
```
1. Read last_user.json → derive profile name → set profile
2. Load saved user from profile DB
3. If user has SignerRelayUrl/SignerRemotePubKey → call ActivateSession()
4. MainViewModel.InitializeAfterLoginAsync() calls RestoreSessionAsync()
5. ExternalSignerService reconnects to Amber relay with saved ephemeral keypair
6. No QR scan needed
```

## Key Files
- `src/OpenChat.Presentation/ViewModels/ShellViewModel.cs` — TryAutoLoginAsync() lines 76-119
- `src/OpenChat.Presentation/ViewModels/MainViewModel.cs` — RestoreSessionAsync() call at lines 315-320
- `src/OpenChat.Core/Services/ExternalSignerService.cs` — RestoreSessionAsync() lines 152-204
- `src/OpenChat.Core/Models/User.cs` — Signer credential properties lines 75-99

## Fix
The `TryAutoLoginAsync()` logic must not gate saved-user loading on `IsCustomProfile`.
After `SetProfile()`, the profile IS custom — ensure the check reflects this, or
bypass the guard entirely and always attempt to load a saved user from the derived profile.

## Steps
- [x] Investigate logs and code
- [ ] Write failing test
- [ ] Fix ShellViewModel.TryAutoLoginAsync() profile derivation
- [ ] Run tests
- [ ] Commit
