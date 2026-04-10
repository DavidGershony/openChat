using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using Google.Android.Material.AppBar;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.FloatingActionButton;
using Google.Android.Material.Snackbar;
using Google.Android.Material.Tabs;
using Google.Android.Material.TextField;
using OpenChat.Android.Adapters;
using OpenChat.Presentation.ViewModels;
using ReactiveUI;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Fragment = AndroidX.Fragment.App.Fragment;

namespace OpenChat.Android.Fragments;

public class ChatListFragment : Fragment
{
    private readonly MainViewModel _mainViewModel;
    private ChatListViewModel ViewModel => _mainViewModel.ChatListViewModel;
    private CompositeDisposable _disposables = new();
    private ChatListAdapter? _adapter;
    private PendingInviteAdapter? _inviteAdapter;

    public ChatListFragment(MainViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
    }

    public override View? OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
    {
        return inflater.Inflate(Resource.Layout.fragment_chat_list, container, false);
    }

    public override void OnViewCreated(View view, Bundle? savedInstanceState)
    {
        base.OnViewCreated(view, savedInstanceState);

        var toolbar = view.FindViewById<MaterialToolbar>(Resource.Id.toolbar)!;
        var searchInput = view.FindViewById<TextInputEditText>(Resource.Id.search_input)!;
        var recyclerView = view.FindViewById<RecyclerView>(Resource.Id.chat_list_recycler)!;
        var loadingIndicator = view.FindViewById<ProgressBar>(Resource.Id.loading_indicator)!;
        var emptyState = view.FindViewById<LinearLayout>(Resource.Id.empty_state)!;
        var fab = view.FindViewById<FloatingActionButton>(Resource.Id.fab_new_chat)!;
        var chatTabs = view.FindViewById<TabLayout>(Resource.Id.chat_tabs)!;

        // Set up tab toggle for Chats / Archived
        var chatsTab = chatTabs.NewTab()!.SetText("Chats")!;
        var archivedTab = chatTabs.NewTab()!.SetText("Archived (0)")!;
        chatTabs.AddTab(chatsTab);
        chatTabs.AddTab(archivedTab);

        ViewModel.WhenAnyValue(x => x.ArchivedChatsCount)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(count => archivedTab.SetText($"Archived ({count})"))
            .DisposeWith(_disposables);

        chatTabs.TabSelected += (s, e) =>
        {
            var showArchived = e.Tab == archivedTab;
            ViewModel.ShowArchivedSection = showArchived;
        };

        // Pending invites views
        var pendingInvitesSection = view.FindViewById<LinearLayout>(Resource.Id.pending_invites_section)!;
        var inviteBadge = view.FindViewById<TextView>(Resource.Id.invite_badge)!;
        var rescanButton = view.FindViewById<MaterialButton>(Resource.Id.rescan_invites_button)!;
        var invitesRecycler = view.FindViewById<RecyclerView>(Resource.Id.pending_invites_recycler)!;

        // Bind toolbar title to HeaderDisplayName (shows npub until metadata loads)
        toolbar.Title = _mainViewModel.HeaderDisplayName;
        _mainViewModel.WhenAnyValue(x => x.HeaderDisplayName)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(name => toolbar.Title = name ?? "OpenChat")
            .DisposeWith(_disposables);

        // Show relay count as toolbar subtitle
        toolbar.Subtitle = _mainViewModel.RelayCountText;
        _mainViewModel.WhenAnyValue(x => x.RelayCountText)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(text => toolbar.Subtitle = text)
            .DisposeWith(_disposables);

        // Set up toolbar menu (use Java listener for reliable click handling)
        toolbar.SetOnMenuItemClickListener(new ActionMenuItemClickListener(item =>
        {
            if (item?.ItemId == Resource.Id.action_reconnect_all)
            {
                _mainViewModel.ReconnectCommand.Execute().Subscribe();
                return true;
            }
            else if (item?.ItemId == Resource.Id.action_my_profile)
            {
                ShowMyProfileDialog();
                return true;
            }
            else if (item?.ItemId == Resource.Id.action_settings)
            {
                (Activity as MainActivity)?.NavigateToSettings();
                return true;
            }
            return false;
        }));

        // Set up Chat RecyclerView
        _adapter = new ChatListAdapter();
        recyclerView.SetLayoutManager(new LinearLayoutManager(Context));
        recyclerView.SetAdapter(_adapter);

        _adapter.ItemClick += (s, chatItem) =>
        {
            ViewModel.SelectedChat = chatItem;
            (Activity as MainActivity)?.NavigateToChat();
        };

        _adapter.ItemLongClick += (s, chatItem) =>
        {
            ShowChatContextMenu(chatItem);
        };

        // Set up Pending Invites RecyclerView
        _inviteAdapter = new PendingInviteAdapter();
        invitesRecycler.SetLayoutManager(new LinearLayoutManager(Context));
        invitesRecycler.SetAdapter(_inviteAdapter);

        _inviteAdapter.AcceptClick += (s, invite) =>
        {
            ViewModel.AcceptInviteCommand.Execute(invite).Subscribe().DisposeWith(_disposables);
        };

        _inviteAdapter.DeclineClick += (s, invite) =>
        {
            ViewModel.DeclineInviteCommand.Execute(invite).Subscribe().DisposeWith(_disposables);
        };

        // Rescan button
        rescanButton.Click += (s, e) =>
        {
            ViewModel.RescanInvitesCommand.Execute().Subscribe().DisposeWith(_disposables);
        };

        // FAB - show bottom sheet with options
        fab.Click += (s, e) =>
        {
            ShowNewOptionsBottomSheet();
        };

        // Search text binding
        searchInput.TextChanged += (s, e) =>
        {
            ViewModel.SearchText = searchInput.Text ?? string.Empty;
        };

        // Observe chats collection and switch based on active tab
        void RefreshChatList()
        {
            Activity?.RunOnUiThread(() =>
            {
                var items = ViewModel.ShowArchivedSection
                    ? ViewModel.ArchivedChats.ToList()
                    : ViewModel.Chats.ToList();
                _adapter.UpdateItems(items);
                emptyState.Visibility = items.Count == 0 ? ViewStates.Visible : ViewStates.Gone;
            });
        }

        ViewModel.Chats.CollectionChanged += (s, e) => RefreshChatList();
        ViewModel.ArchivedChats.CollectionChanged += (s, e) => RefreshChatList();
        ViewModel.WhenAnyValue(x => x.ShowArchivedSection)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => RefreshChatList())
            .DisposeWith(_disposables);

        // Observe pending invites
        ViewModel.PendingInvites.CollectionChanged += (s, e) =>
        {
            Activity?.RunOnUiThread(() =>
            {
                _inviteAdapter.UpdateItems(ViewModel.PendingInvites.ToList());
            });
        };

        // Pending invite count → show/hide section and badge
        ViewModel.WhenAnyValue(x => x.PendingInviteCount)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(count =>
            {
                pendingInvitesSection.Visibility = count > 0 ? ViewStates.Visible : ViewStates.Gone;
                inviteBadge.Text = count.ToString();
            })
            .DisposeWith(_disposables);

        // Rescan loading state
        ViewModel.WhenAnyValue(x => x.IsRescanningInvites)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(scanning =>
            {
                rescanButton.Enabled = !scanning;
                rescanButton.Text = scanning ? "Scanning..." : "Scan";
            })
            .DisposeWith(_disposables);

        // Status message → Snackbar
        ViewModel.WhenAnyValue(x => x.StatusMessage)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Where(msg => !string.IsNullOrEmpty(msg))
            .Subscribe(msg =>
            {
                Snackbar.Make(view, msg!, Snackbar.LengthLong).Show();
                ViewModel.StatusMessage = null;
            })
            .DisposeWith(_disposables);

        // Loading state
        ViewModel.WhenAnyValue(x => x.IsLoading)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(loading =>
            {
                loadingIndicator.Visibility = loading ? ViewStates.Visible : ViewStates.Gone;
            })
            .DisposeWith(_disposables);

        // Observe dialog states for auto-dismiss
        ViewModel.WhenAnyValue(x => x.ShowDeleteChatDialog)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Where(show => !show)
            .Subscribe(_ => { /* dialog auto-dismissed by ViewModel */ })
            .DisposeWith(_disposables);

        ViewModel.WhenAnyValue(x => x.ShowResetGroupDialog)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Where(show => !show)
            .Subscribe(_ => { /* dialog auto-dismissed by ViewModel */ })
            .DisposeWith(_disposables);

        // Initial load
        _adapter.UpdateItems(ViewModel.Chats.ToList());
        _inviteAdapter.UpdateItems(ViewModel.PendingInvites.ToList());
        emptyState.Visibility = ViewModel.Chats.Count == 0 && !ViewModel.IsLoading ? ViewStates.Visible : ViewStates.Gone;
        pendingInvitesSection.Visibility = ViewModel.PendingInviteCount > 0 ? ViewStates.Visible : ViewStates.Gone;
        inviteBadge.Text = ViewModel.PendingInviteCount.ToString();

        // Auto-scan for invites on load
        ViewModel.RescanInvitesCommand.Execute().Subscribe().DisposeWith(_disposables);
    }

    private void ShowNewOptionsBottomSheet()
    {
        if (Context == null) return;

        var items = new[] { "New Chat", "New Group", "Link Device" };
        new MaterialAlertDialogBuilder(Context)
            .SetTitle("Create")!
            .SetItems(items, (s, e) =>
            {
                switch (e.Which)
                {
                    case 0: (Activity as MainActivity)?.NavigateToNewChat(); break;
                    case 1: (Activity as MainActivity)?.NavigateToNewGroup(); break;
                    case 2: (Activity as MainActivity)?.NavigateToAddBot(); break;
                }
            })!
            .Show();
    }

    private void ShowChatContextMenu(ChatItemViewModel chatItem)
    {
        if (Context == null) return;

        var isArchived = ViewModel.ShowArchivedSection;
        var options = new List<(string label, Action action)>();

        if (isArchived)
        {
            options.Add(("Unarchive", () =>
                ViewModel.UnarchiveChatCommand.Execute(chatItem).Subscribe().DisposeWith(_disposables)));
        }
        else
        {
            options.Add(("Archive", () =>
                ViewModel.ArchiveChatCommand.Execute(chatItem).Subscribe().DisposeWith(_disposables)));
        }

        options.Add((chatItem.IsMuted ? "Unmute" : "Mute", () =>
            ViewModel.ToggleMuteCommand.Execute(chatItem).Subscribe().DisposeWith(_disposables)));

        if (chatItem.IsGroup)
            options.Add(("Reset Group", () => ShowResetGroupConfirmation(chatItem)));

        options.Add(("Delete Chat", () => ShowDeleteConfirmation(chatItem)));

        new MaterialAlertDialogBuilder(Context)
            .SetTitle(chatItem.Name)!
            .SetItems(options.Select(o => o.label).ToArray(), (s, e) =>
            {
                options[e.Which].action();
            })!
            .Show();
    }

    private void ShowDeleteConfirmation(ChatItemViewModel chatItem)
    {
        if (Context == null) return;

        new MaterialAlertDialogBuilder(Context)
            .SetTitle("Delete Chat")!
            .SetMessage($"Are you sure you want to delete the chat with {chatItem.Name}?")!
            .SetPositiveButton("Delete", (s, e) =>
            {
                ViewModel.ConfirmDeleteChatCommand.Execute().Subscribe().DisposeWith(_disposables);
            })!
            .SetNegativeButton("Cancel", (s, e) =>
            {
                ViewModel.CancelDeleteChatCommand.Execute().Subscribe().DisposeWith(_disposables);
            })!
            .Show();
    }

    private void ShowResetGroupConfirmation(ChatItemViewModel chatItem)
    {
        if (Context == null) return;

        new MaterialAlertDialogBuilder(Context)
            .SetTitle("Reset Group")!
            .SetMessage($"Are you sure you want to reset the group \"{chatItem.Name}\"? This will re-create the MLS group state.")!
            .SetPositiveButton("Reset", (s, e) =>
            {
                ViewModel.ConfirmResetGroupCommand.Execute().Subscribe().DisposeWith(_disposables);
            })!
            .SetNegativeButton("Cancel", (s, e) =>
            {
                ViewModel.CancelResetGroupCommand.Execute().Subscribe().DisposeWith(_disposables);
            })!
            .Show();
    }

    private void ShowMyProfileDialog()
    {
        if (Context == null) return;

        // Trigger profile metadata loading
        _mainViewModel.ShowMyProfileCommand.Execute().Subscribe().DisposeWith(_disposables);

        var dialogView = LayoutInflater.From(Context)!.Inflate(Resource.Layout.dialog_my_profile, null)!;

        var npubText = dialogView.FindViewById<TextView>(Resource.Id.my_npub_text)!;
        var copyNpubButton = dialogView.FindViewById<ImageButton>(Resource.Id.my_copy_npub_button)!;
        var displayNameText = dialogView.FindViewById<TextView>(Resource.Id.my_display_name_text)!;
        var usernameText = dialogView.FindViewById<TextView>(Resource.Id.my_username_text)!;
        var aboutText = dialogView.FindViewById<TextView>(Resource.Id.my_about_text)!;
        var loadingIndicator = dialogView.FindViewById<ProgressBar>(Resource.Id.my_profile_loading)!;
        var qrImage = dialogView.FindViewById<ImageView>(Resource.Id.my_npub_qr)!;

        var dialog = new MaterialAlertDialogBuilder(Context)
            .SetTitle("My Profile")!
            .SetView(dialogView)!
            .SetPositiveButton("Close", (s, e) =>
            {
                _mainViewModel.CloseMyProfileCommand.Execute().Subscribe().DisposeWith(_disposables);
            })!
            .Create()!;

        dialog.Show();

        copyNpubButton.Click += (s, e) =>
        {
            CopyToClipboard("npub", _mainViewModel.MyNpub);
        };

        // Bind QR code
        _mainViewModel.WhenAnyValue(x => x.MyNpubQrPngBytes)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(bytes =>
            {
                if (bytes != null && bytes.Length > 0)
                {
                    var bitmap = global::Android.Graphics.BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length);
                    qrImage.SetImageBitmap(bitmap);
                    qrImage.Visibility = ViewStates.Visible;
                }
                else
                {
                    qrImage.Visibility = ViewStates.Gone;
                }
            })
            .DisposeWith(_disposables);

        // Bind profile properties
        _mainViewModel.WhenAnyValue(x => x.MyNpub)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(npub => npubText.Text = npub ?? "No key available")
            .DisposeWith(_disposables);

        _mainViewModel.WhenAnyValue(x => x.MyDisplayName)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(name =>
            {
                displayNameText.Text = name ?? "";
                displayNameText.Visibility = string.IsNullOrEmpty(name) ? ViewStates.Gone : ViewStates.Visible;
            })
            .DisposeWith(_disposables);

        _mainViewModel.WhenAnyValue(x => x.MyName)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(name =>
            {
                usernameText.Text = string.IsNullOrEmpty(name) ? "" : $"@{name}";
                usernameText.Visibility = string.IsNullOrEmpty(name) ? ViewStates.Gone : ViewStates.Visible;
            })
            .DisposeWith(_disposables);

        _mainViewModel.WhenAnyValue(x => x.MyAbout)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(about =>
            {
                aboutText.Text = about ?? "";
                aboutText.Visibility = string.IsNullOrEmpty(about) ? ViewStates.Gone : ViewStates.Visible;
            })
            .DisposeWith(_disposables);

        _mainViewModel.WhenAnyValue(x => x.IsLoadingProfile)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(loading =>
            {
                loadingIndicator.Visibility = loading ? ViewStates.Visible : ViewStates.Gone;
            })
            .DisposeWith(_disposables);

        _mainViewModel.WhenAnyValue(x => x.ShowMyProfileDialog)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Where(show => !show)
            .Subscribe(_ => { if (dialog.IsShowing) dialog.Dismiss(); })
            .DisposeWith(_disposables);
    }

    private void CopyToClipboard(string label, string? text)
    {
        if (string.IsNullOrEmpty(text) || Activity == null) return;

        var clipboard = (ClipboardManager?)Activity.GetSystemService(global::Android.Content.Context.ClipboardService);
        if (clipboard != null)
        {
            var clip = ClipData.NewPlainText(label, text);
            clipboard.PrimaryClip = clip;
            Toast.MakeText(Activity, "Copied!", ToastLength.Short)?.Show();
        }
    }

    public override void OnDestroyView()
    {
        _disposables.Dispose();
        _disposables = new CompositeDisposable();
        base.OnDestroyView();
    }
}
