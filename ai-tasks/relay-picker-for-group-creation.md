# Relay Picker UI for Group/Chat Creation

## Goal
When creating a new 1:1 chat or group chat, show a relay selection UI with checkboxes
so the user can choose which relays to include in the MarmotGroupData extension.

## Current Behavior
Connected relays are auto-selected (all of them). No UI to choose.

## Desired Behavior
- Show list of connected relays with checkboxes (all checked by default)
- User can uncheck relays they don't want in this group
- At least one relay must be selected (disable Create button if none selected)
- Selected relays are passed to `CreateGroupAsync(name, relayUrls)`
- The relays become part of the MarmotGroupData extension in the MLS group
- Other members use these relays for publishing/subscribing to group events

## UI Location
- 1:1 chat: in the "New Chat" dialog, below the npub input
- Group chat: in the "Create Group" dialog, below the member list

## Notes
- This is cosmetic/UX — the current fix (auto-select all connected) works functionally
- The relay list is baked into the MLS group at creation time and included in Welcomes
