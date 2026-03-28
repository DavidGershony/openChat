# Relay Picker UI for Group/Chat Creation

## Status: COMPLETED

## What was done
- Added `RelaySelectionItemViewModel` class with `Url` and `IsSelected` properties
- Added `SelectableRelays` ObservableCollection and `SelectedRelayCount` reactive property to ChatListViewModel
- Both New Chat and New Group dialogs now show checkboxes for connected relays (all checked by default)
- Create button is disabled when no relays are selected (wired into canExecute)
- Selected relays (not all connected) are passed to `CreateGroupAsync()`
- Desktop (Avalonia) and Android UI both implemented
- All 267 tests pass
