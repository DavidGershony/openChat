# Link Device — Relay Selection UI

## Status: In Progress

## Goal

Add relay selection to the Link Device dialog with 3 radio button options:
1. **Find user relay (NIP-65)** — auto-discover via kind 10002, fail if not found
2. **Select from list** — show connected relays with checkboxes
3. **Add relay** — manual textbox for a single relay URL

Selected relays are saved to `Chat.RelayUrls` and used for sending gift-wrapped DMs.

## Tasks

- [x] Add ViewModel properties (relay mode enum, relay list, selected relays, manual relay)
- [x] Update desktop AXAML with radio buttons and conditional panels
- [x] Wire up NIP-65 lookup on mode selection
- [x] Pass selected relays through to `GetOrCreateBotChatAsync`
- [x] Update `CreateBotChatAsync` in ChatListViewModel
- [ ] Update Android UI (separate task)
- [ ] Tests
