using Xunit;

namespace OpenChat.Diagnostics;

/// <summary>
/// Manual device test runbook for the Android share target feature.
/// Each test name documents a verification step to perform on a real device.
/// Run these manually after deploying to a phone — they are skipped in CI.
/// </summary>
public class ShareTargetSmokeTests
{
    [Fact(Skip = "Manual device test — share text from Chrome/browser")]
    public void ShareSheet_ShowsOpenChat_WhenSharingText()
    {
        // Steps:
        // 1. Open Chrome, navigate to any page
        // 2. Long-press a URL or tap Share
        // 3. Verify OpenChat appears in the share sheet
        // 4. Tap OpenChat → ShareTargetActivity opens
    }

    [Fact(Skip = "Manual device test — share image from gallery")]
    public void ShareSheet_ShowsOpenChat_WhenSharingImage()
    {
        // Steps:
        // 1. Open Photos/Gallery app
        // 2. Select an image, tap Share
        // 3. Verify OpenChat appears in the share sheet
        // 4. Tap OpenChat → ShareTargetActivity opens with image preview
    }

    [Fact(Skip = "Manual device test — multi-account picker")]
    public void ShareTarget_ShowsAccountPicker_WhenMultipleAccounts()
    {
        // Precondition: 2+ accounts registered in OpenChat
        // Steps:
        // 1. Share any content into OpenChat
        // 2. Verify account selector shows all accounts
        // 3. Verify active account is pre-selected
        // 4. Switch to different account → chat list reloads
    }

    [Fact(Skip = "Manual device test — single account skips picker")]
    public void ShareTarget_SkipsAccountPicker_WhenSingleAccount()
    {
        // Precondition: only 1 account registered
        // Steps:
        // 1. Share any content into OpenChat
        // 2. Verify account picker is hidden or auto-selected
        // 3. Chat list loads directly
    }

    [Fact(Skip = "Manual device test — devices/bots shown first")]
    public void ShareTarget_ShowsDevicesFirst_ThenChats()
    {
        // Precondition: account has both bot/DVM chats and regular DM/group chats
        // Steps:
        // 1. Share content into OpenChat
        // 2. Verify "Devices" section appears at top with bot chats
        // 3. Scroll down → "Chats" section with DMs and groups
        // 4. Verify each section is sorted by most recent activity
    }

    [Fact(Skip = "Manual device test — handoff to main app")]
    public void ShareTarget_HandsOffToMainActivity_WithDraft()
    {
        // Steps:
        // 1. Share text from another app into OpenChat
        // 2. Select a chat in the share activity
        // 3. Verify ShareTargetActivity closes
        // 4. Verify MainActivity opens with the selected chat
        // 5. Verify shared text is pre-filled in the message input box
        // 6. Tap send → message is delivered normally
    }

    [Fact(Skip = "Manual device test — share while app is killed")]
    public void ShareTarget_WorksFromColdStart()
    {
        // Steps:
        // 1. Force-stop OpenChat from Settings
        // 2. Share content from another app
        // 3. Verify ShareTargetActivity loads accounts and chats correctly
        // 4. Select a chat → MainActivity cold-starts and connects relays
        // 5. Shared content appears in message box after relay connection
    }

    [Fact(Skip = "Manual device test — share file attachment")]
    public void ShareTarget_HandlesFileAttachment()
    {
        // Steps:
        // 1. Share a PDF or document from Files app
        // 2. Select a chat in share activity
        // 3. Verify file is staged as attachment in ChatFragment
        // 4. Send → file uploads via Blossom and message delivers
    }
}
