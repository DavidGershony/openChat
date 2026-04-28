using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Widget;
using AndroidX.AppCompat.App;
using AndroidX.RecyclerView.Widget;
using Google.Android.Material.AppBar;
using Google.Android.Material.Chip;
using Microsoft.Extensions.Logging;
using OpenChat.Android.Adapters;
using OpenChat.Core.Configuration;
using OpenChat.Core.Logging;
using OpenChat.Core.Models;
using OpenChat.Core.Services;

namespace OpenChat.Android;

[Activity(Label = "OpenChat", Theme = "@style/AppTheme", Exported = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize,
    ExcludeFromRecents = true, TaskAffinity = "")]
[IntentFilter(
    new[] { Intent.ActionSend },
    Categories = new[] { Intent.CategoryDefault },
    DataMimeType = "*/*")]
[IntentFilter(
    new[] { Intent.ActionSendMultiple },
    Categories = new[] { Intent.CategoryDefault },
    DataMimeType = "*/*")]
public class ShareTargetActivity : AppCompatActivity
{
    private readonly ILogger _logger = LoggingConfiguration.CreateLogger<ShareTargetActivity>();
    private ShareChatAdapter? _adapter;
    private ChipGroup? _chipGroup;
    private TextView? _sharePreview;
    private TextView? _emptyState;
    private RecyclerView? _chatList;
    private IReadOnlyList<AccountEntry> _accounts = Array.Empty<AccountEntry>();
    private string? _selectedAccountPubKey;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        SetTheme(Services.ThemeService.GetSavedStyleResource(this));
        base.OnCreate(savedInstanceState);
        SetContentView(Resource.Layout.activity_share_target);

        // Initialize logging if not already done
        var logDir = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "OpenChat", "logs");
        LoggingConfiguration.Initialize(logDirectory: logDir, perSession: false);

        var toolbar = FindViewById<MaterialToolbar>(Resource.Id.share_toolbar)!;
        toolbar.Title = "Share to OpenChat";
        toolbar.SetNavigationIcon(Resource.Drawable.abc_ic_ab_back_material);
        toolbar.NavigationClick += (s, e) => Finish();

        _chipGroup = FindViewById<ChipGroup>(Resource.Id.account_chip_group)!;
        _sharePreview = FindViewById<TextView>(Resource.Id.share_preview)!;
        _emptyState = FindViewById<TextView>(Resource.Id.share_empty_state)!;
        _chatList = FindViewById<RecyclerView>(Resource.Id.share_chat_list)!;

        _adapter = new ShareChatAdapter();
        _chatList.SetLayoutManager(new LinearLayoutManager(this));
        _chatList.SetAdapter(_adapter);

        _adapter.ChatClick += OnChatSelected;

        // Show share preview
        ShowSharePreview();

        // Load accounts and chats
        LoadAccounts();
    }

    private void ShowSharePreview()
    {
        var text = Intent?.GetStringExtra(Intent.ExtraText);
        if (!string.IsNullOrEmpty(text))
        {
            _sharePreview!.Text = $"Sharing: {text}";
            _sharePreview.Visibility = global::Android.Views.ViewStates.Visible;
        }
        else if (Intent?.Type != null)
        {
            _sharePreview!.Text = $"Sharing: {Intent.Type} file";
            _sharePreview.Visibility = global::Android.Views.ViewStates.Visible;
        }
        else
        {
            _sharePreview!.Visibility = global::Android.Views.ViewStates.Gone;
        }
    }

    private void LoadAccounts()
    {
        AccountRegistryService.Reload();
        _accounts = AccountRegistryService.GetAccounts();

        if (_accounts.Count == 0)
        {
            _logger.LogWarning("No accounts found — cannot share");
            Toast.MakeText(this, "No accounts found. Open OpenChat first.", ToastLength.Short)?.Show();
            Finish();
            return;
        }

        // Resolve which account to pre-select
        var resolved = ShareAccountResolver.Resolve();
        _selectedAccountPubKey = resolved?.PublicKeyHex ?? _accounts[0].PublicKeyHex;

        // Show account chips if multiple accounts
        if (_accounts.Count > 1)
        {
            var container = FindViewById(Resource.Id.account_selector_container)!;
            container.Visibility = global::Android.Views.ViewStates.Visible;

            foreach (var account in _accounts)
            {
                var chip = new Chip(this);
                chip.Text = account.DisplayName ?? account.Npub?[..16] ?? account.PublicKeyHex[..16];
                chip.Checkable = true;
                chip.Checked = account.PublicKeyHex == _selectedAccountPubKey;
                chip.Click += (s, e) =>
                {
                    _selectedAccountPubKey = account.PublicKeyHex;
                    _ = LoadChatsForAccount(account.PublicKeyHex);
                };
                _chipGroup!.AddView(chip);
            }
        }

        _ = LoadChatsForAccount(_selectedAccountPubKey);
    }

    private async Task LoadChatsForAccount(string pubKeyHex)
    {
        try
        {
            var result = await ShareChatLoader.LoadAsync(pubKeyHex);

            RunOnUiThread(() =>
            {
                _adapter!.Update(result);

                if (result.TotalCount == 0)
                {
                    _chatList!.Visibility = global::Android.Views.ViewStates.Gone;
                    _emptyState!.Visibility = global::Android.Views.ViewStates.Visible;
                }
                else
                {
                    _chatList!.Visibility = global::Android.Views.ViewStates.Visible;
                    _emptyState!.Visibility = global::Android.Views.ViewStates.Gone;
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load chats for share target");
            RunOnUiThread(() =>
            {
                Toast.MakeText(this, "Failed to load chats", ToastLength.Short)?.Show();
            });
        }
    }

    private void OnChatSelected(object? sender, Chat chat)
    {
        _logger.LogInformation("Share target: selected chat {ChatId} ({ChatName})", chat.Id, chat.Name);

        var intent = new Intent(this, typeof(MainActivity));
        intent.SetFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);

        // Share handoff extras
        intent.PutExtra("shareAction", true);
        intent.PutExtra("shareChatId", chat.Id);
        intent.PutExtra("shareAccountPubKey", _selectedAccountPubKey);
        intent.PutExtra("shareMimeType", Intent?.Type ?? "text/plain");

        // Pass through the shared content
        var text = Intent?.GetStringExtra(Intent.ExtraText);
        if (text != null)
            intent.PutExtra("shareText", text);

        var uri = Intent?.GetParcelableExtra(Intent.ExtraStream) as global::Android.Net.Uri;
        if (uri != null)
            intent.PutExtra("shareUri", uri.ToString());

        // For SEND_MULTIPLE
        var clipData = Intent?.ClipData;
        if (clipData != null && clipData.ItemCount > 1)
        {
            var uris = new string[clipData.ItemCount];
            for (int i = 0; i < clipData.ItemCount; i++)
                uris[i] = clipData.GetItemAt(i)?.Uri?.ToString() ?? "";
            intent.PutExtra("shareUris", uris);
        }

        StartActivity(intent);
        Finish();
    }
}
