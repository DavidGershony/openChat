# NIP-65 Relay Discovery Implementation

## Status: COMPLETED

## What was implemented

### Core infrastructure (all done):
1. **`RelayPreference` model** (`src/OpenChat.Core/Models/RelayPreference.cs`) — `RelayUsage` enum (Both/Read/Write) and `RelayPreference` class
2. **`NostrConstants`** (`src/OpenChat.Core/NostrConstants.cs`) — centralized `DefaultRelays` and `DiscoveryRelays` constants
3. **All hardcoded relays replaced** — NostrService (5 locations), MainViewModel (2), SettingsViewModel (1) now use `NostrConstants.DefaultRelays`
4. **`UserRelays` table** in StorageService — stores per-user relay preferences with `SaveUserRelayListAsync`/`GetUserRelayListAsync`
5. **`FetchRelayListAsync`** in NostrService — queries discovery relays (purplepag.es, relay.nostr.band) for kind 10002 events
6. **`PublishRelayListAsync`** in NostrService — publishes kind 10002 replaceable event with relay preferences
7. **Login flow** — `MainViewModel.InitializeAfterLoginAsync` checks saved relays → fetches NIP-65 from discovery → falls back to defaults
8. **Reconnect flow** — uses saved relay list when available
9. **SettingsViewModel** — add/remove relays saves to storage, cycle relay usage (Both→Read→Write), publish relay list button
10. **RelayViewModel** — added reactive `Usage` property with `UsageLabel` (ObservableAsPropertyHelper)
11. **Settings UI** — relay usage toggle button, "Publish Relay List (NIP-65)" button in SettingsView.axaml

### Tests (6 new, all passing):
- `RelayList_AddRemoveCycleUsage_PersistsToStorage` — add, cycle usage, remove relays with storage verification
- `PublishRelayList_SendsNip65Event` — verifies NIP-65 publish mock is called
- `Login_WithSavedRelays_UsesThemInsteadOfDefaults` — pre-saves custom relays, verifies they're used on login

### Remaining (future work):
- Use target user's NIP-65 relays when fetching KeyPackages and sending Welcomes (requires plumbing relay discovery into `FetchKeyPackagesAsync`/`PublishWelcomeAsync`)
- Include write relays in KeyPackage `relays` tag
- Publish NIP-65 on new key generation

## Files changed
- `src/OpenChat.Core/Models/RelayPreference.cs` (NEW)
- `src/OpenChat.Core/NostrConstants.cs` (NEW)
- `src/OpenChat.Core/Services/INostrService.cs` (added FetchRelayListAsync, PublishRelayListAsync)
- `src/OpenChat.Core/Services/IStorageService.cs` (added SaveUserRelayListAsync, GetUserRelayListAsync)
- `src/OpenChat.Core/Services/NostrService.cs` (replaced hardcoded relays, added NIP-65 fetch/publish)
- `src/OpenChat.Core/Services/StorageService.cs` (added UserRelays table, implemented storage methods)
- `src/OpenChat.Presentation/ViewModels/MainViewModel.cs` (NIP-65 login/reconnect flow)
- `src/OpenChat.Presentation/ViewModels/SettingsViewModel.cs` (relay usage cycling, publish, persistence)
- `src/OpenChat.UI/Views/SettingsView.axaml` (usage toggle, publish button)
- `tests/OpenChat.UI.Tests/HeadlessTestBase.cs` (mock setups for new methods)
- `tests/OpenChat.UI.Tests/HeadlessSettingsTests.cs` (3 new NIP-65 tests)
