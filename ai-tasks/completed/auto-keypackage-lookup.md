# Auto KeyPackage Lookup on Chat/Group Creation

## Goal
Remove the manual "Look up KeyPackage" button and automatically fetch KeyPackages when the user enters an npub. Show a minimal list of found packages with metadata, pre-selecting the most recent one.

## Current Flow
1. User enters npub in "New Chat" or "New Group" dialog
2. User clicks "Look up KeyPackage" button manually
3. If found, a "Create" button appears
4. User clicks "Create"

## New Flow
1. User enters npub in "New Chat" or "New Group" dialog
2. KeyPackage lookup starts automatically (debounced, after valid npub detected)
3. Show a compact list of found KeyPackages with:
   - Created timestamp (relative, e.g. "2 hours ago")
   - Ciphersuite (e.g. "0x0001")
   - Source relay
   - KeyPackage ref (i-tag, truncated)
4. Most recent KeyPackage selected by default (highlighted)
5. User can select a different one if needed
6. "Create" button enabled once a KeyPackage is selected

## Requirements
- [x] Remove "Look up KeyPackage" button from both New Chat and New Group dialogs
- [x] Auto-trigger lookup when npub input changes (debounce 500ms, validate npub format first)
- [x] Show loading spinner during lookup
- [x] Display found KeyPackages in a compact list:
  - Each item: relay icon + created time + ciphersuite badge
  - Selected item highlighted
  - Most recent auto-selected
- [x] Show "No KeyPackages found" if none available
- [x] Show error if npub is invalid
- [x] For New Group: auto-lookup per member line (comma-separated npubs)
- [x] KeyPackage model may need `CreatedAt` and `RelaySource` fields if not already present (already had them)

## Technical Notes
- Use `WhenAnyValue` + `Throttle(500ms)` on the npub input for auto-trigger
- `FetchKeyPackagesAsync` already returns `IEnumerable<KeyPackage>` — check what metadata is available
- The `KeyPackage` model has `NostrEventId`, `Data`, `OwnerPublicKey`, `CiphersuiteId`, `NostrTags`
- Created timestamp may be in the Nostr event's `created_at` — check if we store it

## Files to modify
- `src/OpenChat.Presentation/ViewModels/ChatListViewModel.cs` (auto-lookup logic)
- `src/OpenChat.UI/Views/ChatListView.axaml` (remove button, add KeyPackage list)
- `src/OpenChat.Core/Models/KeyPackage.cs` (add CreatedAt/RelaySource if needed)
- `src/OpenChat.Core/Services/NostrService.cs` (may need to return more metadata from fetch)

## Status
- [x] Complete
