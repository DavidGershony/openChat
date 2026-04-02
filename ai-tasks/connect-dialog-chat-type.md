# Connect Dialog — Chat Type Selection & Avatar Updates

## Goal
Refactor the "AI Connect" dialog to support both Bot and DM chat types, and update chat list avatars to use full-circle icons.

## Steps

- [x] Read existing code (dialog, viewmodel, chat list, models)
- [x] Add `IsBotChatType` reactive property and `IsDm` to ChatItemViewModel
- [x] Update `CreateBotChatAsync` to branch on selected chat type
- [x] Add radio buttons to dialog XAML — desktop + Android
- [x] Update chat list avatar: full-circle robot for Bot, "DM" text for DirectMessage — desktop + Android
- [x] Remove small bot badge (replaced by full-circle icon)
- [x] Run unit tests (284 passed, 0 failed)
- [ ] Commit
