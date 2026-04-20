# OpenChat Integration Testing Guidelines

## Overview

The `OpenChat.UI.Tests` project contains headless integration tests for the OpenChat application. These tests simulate real user interactions with the UI to verify the complete functionality of the messaging application, including MLS group chat, direct messaging, login/logout flows, and relay communication.

## General Guidelines

1. **Stay High-Level**: Tests should simulate user interactions by loading ViewModels, populating input fields, and executing commands to trigger actions.

2. **Verify UI State**: Include assertions to check that the correct values are set on UI elements such as text fields, buttons, chat lists, and message displays.

3. **Impersonate a User**: Tests should mimic how a real user would interact with OpenChat, including logging in with keys, creating/joining groups, sending messages, and managing contacts.

## What We Are Testing

The current test suite covers the following scenarios:

1. **HeadlessLoginTests**: Tests the complete login flow including private key import, new key generation, and logout with state cleanup.

2. **HeadlessMlsMessagingTests**: Tests MLS-based group messaging including group creation, message encryption/decryption, and multi-member communication.

3. **HeadlessChatManagementTests**: Tests chat lifecycle operations — creating, archiving, and deleting conversations.

4. **HeadlessChatUITests**: Tests UI-level chat interactions like selecting chats, scrolling messages, and input state.

5. **HeadlessGroupLifecycleTests**: Tests MLS group creation, member addition/removal, and group state persistence.

6. **HeadlessGroupMemberTests**: Tests member management including invites, key package handling, and participant lists.

7. **HeadlessGroupParticipantTests**: Tests participant-level operations within group chats.

8. **HeadlessMessageOperationsTests**: Tests message-level operations such as replies, reactions, and media attachments.

9. **HeadlessMessagingExtendedTests**: Tests extended messaging scenarios including offline queuing and multi-device sync.

10. **HeadlessProfileAndRelayTests**: Tests profile editing, relay configuration, and connection management.

11. **HeadlessDmAndBotTests**: Tests direct message flows and bot interactions.

12. **HeadlessSettingsTests**: Tests settings UI including relay list management and preference toggles.

13. **HeadlessRealRelayTests**: Tests against real Nostr relays (requires network access).

14. **HeadlessRealMlsIntegrationTests**: End-to-end MLS tests using real relay infrastructure.

15. **HeadlessIntegrationTests**: Full-stack integration tests covering login-to-messaging flows.

## What Can Still Be Tested

1. **Close/Reopen Group Chat**: Desktop close and reopen scenario where MLS state must persist across app restarts.

2. **Multi-Device Sync**: Verifying message delivery and MLS state when the same user is on multiple devices.

3. **Error Handling**: Tests for scenarios such as invalid keys, relay disconnections, failed MLS commits, and network timeouts.

4. **Performance**: Tests to measure message encryption/decryption throughput, relay connection latency, and large group scalability.

5. **External Signer (Amber)**: Tests for NIP-46/NIP-55 external signing flows on desktop and Android.

6. **Media Handling**: Tests for MIP-04 encrypted media upload/download via Blossom servers.

7. **Additional User Flows**: Complex multi-user scenarios such as concurrent group joins, relay failover, and invite recovery.

## Best Practices

1. **Use Dual-Backend Testing**: Test with both `"rust"` and `"managed"` MLS backends using `[InlineData]` theory parameters to ensure both paths work correctly.

2. **Extend HeadlessTestBase**: All headless tests should inherit from `HeadlessTestBase` which provides `CreateRealContext()`, native DLL checks, and automatic DB cleanup.

3. **Log Actions**: Use logging to track the progress of tests and aid in debugging. Prefer injected `ILogger`-based logging in production and shared code so that log output is structured and routed through the standard logging pipeline. If you use `Console.WriteLine` for quick diagnostic output during investigation, treat it as temporary — remove it before committing. However, if a diagnostic log turns out to be genuinely useful (e.g., it logs state that would help diagnose future failures), convert it to an `ILogger` call and keep it rather than discarding it.

4. **Handle Asynchronous Operations**: Use `async/await` and `Dispatcher.UIThread.RunJobs()` to process Avalonia's dispatcher queue. Add appropriate `Task.Delay()` for operations that need time to propagate (relay communication, MLS state updates).

5. **Clean Up Resources**: The `HeadlessTestBase.Dispose()` method handles SQLite pool clearing and temp DB deletion. Add any additional disposables to the `Disposables` list.

6. **Check Native DLL Availability**: Use `ShouldSkip(backend)` at the top of tests that require the Rust native DLL, so tests gracefully skip on machines without the native library.

7. **Add Diagnostic Logs Before Assertions**: Before any assertion that could fail, log the current state of the relevant data (e.g., chat counts, message content, group member lists). This makes it possible to understand *why* a test failed from the log output alone, without needing to reproduce and debug interactively.

8. **Use Mocks for External Services**: Use `Mock<INostrService>` and the `EventsSubject` to simulate relay events without requiring network access. Reserve real relay tests for dedicated integration test classes.

9. **Never Silently Swallow Errors**: Do not catch exceptions and only log them. Every `catch` block in a test must either rethrow, assert, or track the error for a later assertion. A test that logs a failure but reports green is worse than no test — it creates false confidence.

10. **Verify Via Storage, Not Manual Decrypt**: When testing the relay message flow, verify that messages arrived by checking storage (what the user sees), not by manually calling `DecryptMessageAsync` on relay-fetched events. `MessageService.InitializeAsync` subscribes to all `NostrService.Events` (line 69 of MessageService.cs), so `HandleGroupMessageEventAsync` automatically decrypts incoming kind-445 events. Manually re-decrypting the same events fails because the MLS ratchet state has already been consumed ("Generation N has already been used"). The correct pattern:
    ```csharp
    // WRONG — bypasses the real pipeline, double-processes MLS state
    var events = await FetchRawEventsFromRelay(relayUrl, filter);
    foreach (var ev in events)
        await mlsService.DecryptMessageAsync(groupId, eventBytes); // fails: ratchet consumed

    // RIGHT — matches real app flow
    await sender.MessageService.SendMessageAsync(chatId, "Hello");
    await Task.Delay(5000); // wait for relay + subscription delivery
    var msgs = await receiver.Storage.GetMessagesForChatAsync(receiverChatId);
    Assert.True(msgs.Any(m => m.Content.Contains("Hello")));
    ```

11. **Symmetric Subscriptions**: In multi-user E2E tests, all users must have the same subscription setup — just as the real app's `ChatListViewModel` subscribes every user to their group's messages. Giving one user a subscription and not another creates asymmetric MLS state that doesn't reflect production behavior and produces misleading results (one user appears to fail while the other succeeds, when in reality both work).

12. **Assert on Every Expected Outcome**: Every test step that produces a result must have a corresponding assertion. If a test checks "WN saw Alice's message" and "Alice saw WN's message", both directions must be asserted — not just logged. Use `Assert.True` with a descriptive message that includes the actual state, so failures are self-diagnosing from the output alone.

## Example Test Structure

```csharp
[AvaloniaTheory]
[InlineData("rust")]
[InlineData("managed")]
public async Task SendMessage_AppearsInChatHistory(string backend)
{
    if (ShouldSkip(backend)) return;
    Log($"========== STARTING SendMessage_AppearsInChatHistory [{backend}] ==========");

    // Arrange: Create a real MLS context with storage and services
    var ctx = await CreateRealContext(backend);
    await ctx.MessageService.InitializeAsync();

    var mainVm = CreateMainViewModel(ctx);
    Dispatcher.UIThread.RunJobs();

    // Act: Create a group and send a message
    var groupInfo = await ctx.MlsService.CreateGroupAsync("Test Group", new[] { "wss://relay.test" });
    var chat = new Chat
    {
        Id = Guid.NewGuid().ToString(),
        Name = "Test Group",
        Type = ChatType.Group,
        MlsGroupId = groupInfo.GroupId,
        CreatedAt = DateTime.UtcNow,
        LastActivityAt = DateTime.UtcNow
    };
    await ctx.Storage.SaveChatAsync(chat);
    await mainVm.ChatListViewModel.LoadChatsAsync();
    Dispatcher.UIThread.RunJobs();

    // Assert: Verify chat appears in list
    Assert.NotEmpty(mainVm.ChatListViewModel.Chats);
    Assert.Contains(mainVm.ChatListViewModel.Chats, c => c.Name == "Test Group");

    Log($"========== SendMessage_AppearsInChatHistory [{backend}] PASSED ==========");
}
```

## Running Tests

To run the UI integration tests:

```bash
dotnet test tests/OpenChat.UI.Tests/OpenChat.UI.Tests.csproj
```

To run the core/service-level tests:

```bash
dotnet test tests/OpenChat.Core.Tests/OpenChat.Core.Tests.csproj
```

To run all tests:

```bash
dotnet test
```

## Debugging Tests

- Use logging to track the progress of tests and identify issues.
- Run tests with detailed logging to see the output:

```bash
dotnet test tests/OpenChat.UI.Tests/OpenChat.UI.Tests.csproj --logger "console;verbosity=detailed"
```

- For Rust backend issues, ensure `openchat_native.dll` is present in the test output directory.
- For relay-dependent tests, verify network connectivity and that test relays are accessible.

## Test Project Structure

| Project | Purpose |
|---------|---------|
| `tests/OpenChat.UI.Tests` | Headless Avalonia integration tests (UI + ViewModel + Services) |
| `tests/OpenChat.Core.Tests` | Unit/integration tests for core services (MLS, Nostr, Storage, Crypto) |
| `tests/OpenChat.Diagnostics` | Diagnostic utilities for debugging test infrastructure |

## Contributing

When adding new tests, follow the existing patterns and guidelines to ensure consistency and maintainability:

- Inherit from `HeadlessTestBase` for UI tests
- Use `[AvaloniaTheory]` with `[InlineData("rust")]` / `[InlineData("managed")]` for dual-backend coverage
- Add descriptive log messages at test start/end and before assertions
- Use `[Skip]` attribute with a reason when tests are blocked on pending refactors
