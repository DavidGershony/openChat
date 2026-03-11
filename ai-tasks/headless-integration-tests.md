# Task: Comprehensive Headless Integration Tests with Real MLS

## Status: In Progress

## Goal

Add headless Avalonia integration tests using real MLS services (both Rust and Managed backends via `[AvaloniaTheory]`) for every meaningful app feature. Tests use real `StorageService`, real `MessageService`, real `IMlsService`, with mocked `INostrService` (no relay needed).

## Existing Tests (11) — DONE

File: `tests/OpenChat.UI.Tests/HeadlessRealMlsIntegrationTests.cs`

- [x] LoginFlow_SetsIsLoggedIn_MainUIBecomesVisible
- [x] MainWindow_WhenNotLoggedIn_LoginViewIsVisible
- [x] PendingInvite_ArrivesViaObservable_AppearsInChatList
- [x] ChatSelection_LoadsChatInChatViewModel
- [x] NewGroupDialog_OpensAndBindsGroupName
- [x] SettingsNavigation_TogglesCurrentView
- [x] FullFlow_Login_CreateGroup_AppearsInChatList
- [x] ResetGroup_RemovesChatFromList
- [x] CancelResetGroup_KeepsChatInList
- [x] DecryptionError_SurfacesStatusMessage
- [x] ChatListView_RendersPendingInvites

## Batch 1: Login & Key Management (3 tests)

File: `tests/OpenChat.UI.Tests/HeadlessLoginTests.cs`

- [ ] ImportPrivateKey_LogsInAndShowsMainUI
- [ ] GenerateNewKey_CreatesValidKeysAndLogsIn
- [ ] Logout_ClearsStateAndShowsLogin

## Batch 2: Messaging (3 tests)

File: `tests/OpenChat.UI.Tests/HeadlessMlsMessagingTests.cs`

- [ ] SendMessage_InGroup_EncryptsViaRealMls
- [ ] ReceiveGroupMessage_DecryptsAndAppearsInChat (two-user MLS)
- [ ] MultipleGroups_SwitchBetween_LoadsCorrectMessages

## Batch 3: Invite & Group Lifecycle (4 tests)

File: `tests/OpenChat.UI.Tests/HeadlessGroupLifecycleTests.cs`

- [ ] AcceptInvite_ProcessesWelcome_CreatesChat (two-user MLS)
- [ ] DeclineInvite_RemovesFromPendingList
- [ ] CreateGroup_WithInvite_PublishesWelcome (two-user MLS)
- [ ] RescanInvites_FindsMissedWelcomes

## Batch 4: Settings & KeyPackage (3 tests)

File: `tests/OpenChat.UI.Tests/HeadlessSettingsTests.cs`

- [ ] PublishKeyPackage_UpdatesStatusInSettings
- [ ] AuditKeyPackages_ShowsResults
- [ ] SaveProfile_PersistsAndReloads

## Batch 5: Chat Management (4 tests)

File: `tests/OpenChat.UI.Tests/HeadlessChatManagementTests.cs`

- [ ] DeleteChat_RemovesFromList
- [ ] ChatSearch_FiltersChats
- [ ] PinChat_MovesToTop
- [ ] MuteChat_TogglesState

## Batch 6: UI Details (4 tests)

File: `tests/OpenChat.UI.Tests/HeadlessUIDetailsTests.cs`

- [ ] LoadMoreMessages_PaginatesCorrectly
- [ ] ContactMetadataPanel_OpensAndShowsInfo
- [ ] UnreadCount_IncrementsOnNewMessage
- [ ] RelayConnectionStatus_UpdatesUI

## Future (not in this task)

- [ ] NIP-46 External Signer flow (separate task, needs planning)

## Notes

- All tests use `[AvaloniaTheory]` with `[InlineData("rust")]` and `[InlineData("managed")]`
- Rust tests skip gracefully when `openchat_native.dll` is absent
- Two-user MLS tests require creating two separate `RealTestContext` instances
- Each batch: implement → run tests → commit → next batch
- Shared test helpers extracted into `HeadlessTestBase.cs` to avoid duplication
