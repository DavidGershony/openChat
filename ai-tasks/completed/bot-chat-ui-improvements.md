# Bot Chat UI Improvements

## 1. Bot icon indicator not showing on desktop chat list

The bot icon badge was added to `ChatListView.axaml` (the `IsBot` binding with robot PathIcon), but it may not be visible due to:
- The chat item might need the `IsBot` property set correctly in `ChatItemViewModel.Update()`
- Verify `chat.Type == ChatType.Bot` is actually stored in the database when created via AI Connect
- Check if the avatar panel layout clips the bot icon (same position as group icon)

**Files to check:**
- `src/OpenChat.UI/Views/ChatListView.axaml` — bot icon Border with `IsVisible="{Binding IsBot}"`
- `src/OpenChat.Presentation/ViewModels/ChatListViewModel.cs` — `ChatItemViewModel.Update()` sets `IsBot = chat.Type == ChatType.Bot`

## 2. Show connection progress when sending first bot message

When creating a new MLS group chat, the UI shows a progress overlay ("Sending invite...", "Publishing KeyPackage...", etc.) via `CreateProgress` property and the sending overlay in the dialog. The bot chat needs the same UX so the user knows what's happening.

**What to implement:**
- After tapping "Connect" in the AI Connect dialog, show a progress overlay (same pattern as New Chat dialog)
- Steps to show: "Connecting to bot..." → "Publishing encrypted message..." → "Waiting for response..."
- Reuse the existing `CreateProgress` property or add a `BotConnectProgress` property
- The dialog should stay open with the progress overlay until the first message is successfully published
- On success, close dialog and navigate to the bot chat
- On failure, show error in the dialog

**Pattern to follow:**
- `src/OpenChat.UI/Views/MainWindow.axaml` — New Chat dialog has `new_chat_sending_overlay` with ProgressBar + status text
- `src/OpenChat.Android/Fragments/NewChatFragment.cs` — `CreateChatCommand.IsExecuting` toggles form/overlay visibility
- `src/OpenChat.Android/Resources/layout/fragment_new_chat.xml` — has `new_chat_sending_overlay` LinearLayout

**Desktop (MainWindow.axaml):**
- Add sending overlay to the Add Bot dialog (currently only has input + buttons)
- Bind to `CreateBotChatCommand.IsExecuting` for visibility toggle

**Android (AddBotFragment.cs):**
- Already has basic `IsExecuting` binding that changes button text to "Adding..."
- Needs full overlay with ProgressBar like NewChatFragment has

**Android (fragment_add_bot.xml):**
- Add sending overlay section matching `fragment_new_chat.xml` pattern
