# Task: Comprehensive Headless Integration Tests with Real MLS

## Status: Complete

## Goal

Add headless Avalonia integration tests using real MLS services (both Rust and Managed backends via `[AvaloniaTheory]`) for every meaningful app feature. Tests use real `StorageService`, real `MessageService`, real `IMlsService`, with mocked `INostrService` (no relay needed).

## Test Summary: 29 tests (88 total with both backends)

All 88 tests passing.

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

## Batch 1: Login & Key Management (3 tests) — DONE

File: `tests/OpenChat.UI.Tests/HeadlessLoginTests.cs`

- [x] ImportPrivateKey_LogsInAndShowsMainUI
- [x] GenerateNewKey_CreatesValidKeysAndLogsIn
- [x] Logout_ClearsStateAndShowsLogin

## Batch 2: Messaging (3 tests) — DONE

File: `tests/OpenChat.UI.Tests/HeadlessMlsMessagingTests.cs`

- [x] SendMessage_InGroup_EncryptsViaRealMls
- [x] ReceiveGroupMessage_DecryptsAndAppearsInChat (two-user MLS)
- [x] MultipleGroups_SwitchBetween_LoadsCorrectMessages

## Batch 3: Invite & Group Lifecycle (4 tests) — DONE

File: `tests/OpenChat.UI.Tests/HeadlessGroupLifecycleTests.cs`

- [x] AcceptInvite_ProcessesWelcome_CreatesChat (two-user MLS)
- [x] DeclineInvite_RemovesFromPendingList
- [x] CreateGroup_WithInvite_PublishesWelcome (two-user MLS)
- [x] RescanInvites_FindsMissedWelcomes

## Batch 4: Settings & KeyPackage (3 tests) — DONE

File: `tests/OpenChat.UI.Tests/HeadlessSettingsTests.cs`

- [x] PublishKeyPackage_UpdatesStatusInSettings
- [x] AuditKeyPackages_ShowsResults
- [x] SaveProfile_PersistsAndReloads

## Batch 5: Chat Management (5 tests) — DONE

File: `tests/OpenChat.UI.Tests/HeadlessChatManagementTests.cs`

- [x] DeleteChat_RemovesFromList
- [x] ChatSearch_FiltersChats
- [x] UnreadCount_IncrementsOnNewMessage (two-user MLS)
- [x] LoadMoreMessages_PaginatesCorrectly
- [x] ContactMetadataPanel_OpensAndShowsInfo

## Dropped (overkill or not exposed as commands)

- PinChat_MovesToTop — No pin command in ChatListViewModel, only a model property
- MuteChat_TogglesState — No mute command in ChatListViewModel, only a model property
- RelayConnectionStatus_UpdatesUI — Requires mocking complex observable sequences

## Future (separate task)

- [ ] NIP-46 External Signer flow (needs planning)

## Notes

- All tests use `[AvaloniaTheory]` with `[InlineData("rust")]` and `[InlineData("managed")]`
- Rust tests skip gracefully when `openchat_native.dll` is absent
- Two-user MLS tests use `PrepareKeyPackageForAddMember()` helper
- Shared infrastructure in `HeadlessTestBase.cs`
- Two-user MLS decrypt tests use ChatViewModel directly (not MainViewModel) to avoid MLS state re-initialization
