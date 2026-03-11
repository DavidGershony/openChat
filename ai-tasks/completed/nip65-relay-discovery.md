# NIP-65 Relay Discovery Implementation

## Status: COMPLETED

## What was implemented

### Core infrastructure:
1. **`RelayPreference` model** — `RelayUsage` enum (Both/Read/Write) and `RelayPreference` class
2. **`NostrConstants`** — centralized `DefaultRelays` and `DiscoveryRelays` constants
3. **All hardcoded relays replaced** — NostrService (5), MainViewModel (2), SettingsViewModel (1)
4. **`UserRelays` table** in StorageService — per-user relay persistence
5. **`FetchRelayListAsync`** — queries discovery relays for kind 10002 events
6. **`PublishRelayListAsync`** — publishes kind 10002 with relay preferences

### Login flow:
7. Check saved relays → fetch NIP-65 from discovery → fall back to defaults
8. Auto-publish NIP-65 relay list for new users (no existing relay list found)
9. Reconnect uses saved relay list

### Target user relay discovery:
10. **`FetchKeyPackagesAsync`** — discovers target user's NIP-65 write relays first, then also checks connected/default relays
11. **`PublishWelcomeAsync`** — discovers recipient's NIP-65 read relays, connects to them, sends Welcome there too
12. KeyPackage `relays` tag already uses connected relays (which reflect user's NIP-65 list after login)

### Settings UI:
13. Add/remove relays auto-saves to storage
14. Cycle relay usage (Both→Read→Write) per relay
15. "Publish Relay List (NIP-65)" button
16. Reactive `UsageLabel` on `RelayViewModel`

### Tests (6 new, all passing):
- `RelayList_AddRemoveCycleUsage_PersistsToStorage`
- `PublishRelayList_SendsNip65Event`
- `Login_WithSavedRelays_UsesThemInsteadOfDefaults`

## Files changed
- `src/OpenChat.Core/Models/RelayPreference.cs` (NEW)
- `src/OpenChat.Core/NostrConstants.cs` (NEW)
- `src/OpenChat.Core/Services/INostrService.cs`
- `src/OpenChat.Core/Services/IStorageService.cs`
- `src/OpenChat.Core/Services/NostrService.cs`
- `src/OpenChat.Core/Services/StorageService.cs`
- `src/OpenChat.Presentation/ViewModels/MainViewModel.cs`
- `src/OpenChat.Presentation/ViewModels/SettingsViewModel.cs`
- `src/OpenChat.UI/Views/SettingsView.axaml`
- `tests/OpenChat.UI.Tests/HeadlessTestBase.cs`
- `tests/OpenChat.UI.Tests/HeadlessSettingsTests.cs`
