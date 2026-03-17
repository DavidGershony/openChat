# Real Relay Group Chat Integration Tests

## Goal
Add real-relay integration test variants of the existing headless A->B tests.
Same test logic, but using a real NostrService connected to ws://localhost:7777 instead of mocks.

## Steps
- [x] Read reference files (HeadlessTestBase, HeadlessRealMlsIntegrationTests, CrossMdkRelayIntegrationTests, RelayIntegrationTests, NostrService, MessageService, INostrService, ManagedMlsService)
- [x] Create `tests/OpenChat.Core.Tests/RealRelayGroupChatTests.cs` with IAsyncLifetime
- [x] Verify build succeeds (0 warnings, 0 errors)
- [ ] Move to completed

## Tests
1. `FullGroupChat_CreateInviteAcceptAndExchangeMessages` - Full A->B flow with real relay
2. `PublishedEvent_HasCorrectNostrGroupId` - Protocol compliance check
3. `WelcomeEvent_NotRejectedByRelay` - Catches timestamp bugs
