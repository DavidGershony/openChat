# Android Share Target for OpenChat

## Goal

When a user clicks "Share" in any Android app, OpenChat appears as an option. Selecting it opens a dedicated lightweight activity where the user picks an account and a chat, then the app opens with the shared content ready to send.

## Requirements

1. **Dedicated `ShareTargetActivity`** — lightweight, no relay connections
2. **Multi-account support** — account picker + "default share account" setting (configurable in Settings)
3. **Two-section chat layout** — Devices (Bot/DVM) listed first, then DM/Group chats below
4. **All MIP-04 content types** — text/plain, image/*, audio/*, and generic file/* (all MIME types accepted)
5. **Hand off to MainActivity** — after chat selection, open the full app with content pre-filled as draft; user sends manually

## Architecture

```
Other App -> Share Sheet -> ShareTargetActivity -> MainActivity -> ChatFragment (with draft)
               (Android)    (account + chat        (relay          (content pre-filled,
                              picker, no relays)    connect)        user hits send)
```

## Files to Create

### 1. `src/OpenChat.Android/ShareTargetActivity.cs`

- `[IntentFilter]` attributes for `ACTION_SEND` / `ACTION_SEND_MULTIPLE` with `*/*` MIME type
- Layout: account selector at top, "Devices" section, "Chats" section in scrollable list
- Loads accounts from `AccountRegistryService.GetAccounts()`
- Pre-selects default share account (from global setting) or active account
- On account change: opens read-only `StorageService` for that profile's DB, queries chats
- On chat tap: launches `MainActivity` intent with extras (`shareAction`, `shareChatId`, `shareAccountPubKey`, `shareText`/`shareUri`/`shareUris`, `shareMimeType`)
- Finishes after launching

### 2. `src/OpenChat.Android/Layouts/activity_share_target.xml`

- Account selector (horizontal chips or spinner)
- "Devices" header + RecyclerView
- "Chats" header + RecyclerView
- All in NestedScrollView

### 3. `src/OpenChat.Android/Adapters/ShareChatAdapter.cs`

- Lightweight adapter: avatar, name, last message preview
- Click handler returns selected chat ID + type

## Files to Modify

### 4. `src/OpenChat.Android/MainActivity.cs`

- Add `LaunchMode = LaunchMode.SingleTop` to activity attribute
- Override `OnNewIntent()` for warm-start share
- Detect `shareAction` extra in `OnCreate` + `OnNewIntent`
- Switch account if `shareAccountPubKey` differs from current
- Navigate to `ChatFragment` with shared content in Bundle

### 5. `src/OpenChat.Android/Fragments/ChatFragment.cs`

- Check for share extras in arguments on creation
- Pre-fill message box (text) or stage attachment (media/file)
- User reviews and taps send (existing `SendMediaMessageAsync` / `SendTextMessageAsync` pipeline)

## Data Flow

1. Share intent -> `ShareTargetActivity.OnCreate`
2. Extract intent data (`ExtraText`, `ExtraStream`, `ClipData`)
3. Load accounts, pre-select active account (skip picker if single account)
4. On account select: open read-only StorageService for profile DB, query chats by type
5. Display bots first, then DM/group chats sorted by LastActivityAt
6. User taps chat -> build MainActivity intent with extras -> StartActivity -> Finish()
7. MainActivity receives -> SwitchAccountAsync if needed -> navigate to ChatFragment with draft
8. ChatFragment pre-fills content -> user sends manually

## Account Loading in ShareTargetActivity

Lightweight read-only approach (no full app init):
```
ProfileConfiguration.DeriveProfileName(pubKeyHex)
-> DB path: {RootDataDir}/profiles/{profileName}/openchat.db
-> open read-only StorageService
-> GetAllChatsAsync() -> partition by Type (Bot first, then DM/Group)
```

## Account Selection

- Pre-selects the currently active account from `AccountRegistryService.GetActiveAccount()`
- If only one account exists: skip account picker entirely
- Account picker shown at top of share activity for multi-account users

## Nice to Have: Default Share Account Setting

If "always use active account" becomes annoying, add a setting to override the pre-selection:
- Add `DefaultShareAccountPubKey` to `AccountRegistry` model + `AccountRegistryService`
- Toggle in SettingsFragment: "Set as default share account"
- ~30 min work since all plumbing already exists