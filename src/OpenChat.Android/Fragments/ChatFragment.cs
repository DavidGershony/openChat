using Android;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using AndroidX.RecyclerView.Widget;
using Google.Android.Material.AppBar;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.FloatingActionButton;
using Google.Android.Material.TextField;
using OpenChat.Android.Adapters;
using OpenChat.Presentation.ViewModels;
using ReactiveUI;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Fragment = AndroidX.Fragment.App.Fragment;

namespace OpenChat.Android.Fragments;

public class ChatFragment : Fragment
{
    private readonly MainViewModel _mainViewModel;
    private ChatViewModel ViewModel => _mainViewModel.ChatViewModel;
    private CompositeDisposable _disposables = new();
    private MessageAdapter? _adapter;
    private LinearLayoutManager? _layoutManager;

    public ChatFragment(MainViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
    }

    public override View? OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
    {
        return inflater.Inflate(Resource.Layout.fragment_chat, container, false);
    }

    public override void OnViewCreated(View view, Bundle? savedInstanceState)
    {
        base.OnViewCreated(view, savedInstanceState);

        var toolbar = view.FindViewById<MaterialToolbar>(Resource.Id.chat_toolbar)!;
        var recyclerView = view.FindViewById<RecyclerView>(Resource.Id.messages_recycler)!;
        var messageInput = view.FindViewById<TextInputEditText>(Resource.Id.message_input)!;
        var sendButton = view.FindViewById<FloatingActionButton>(Resource.Id.send_button)!;

        // Toolbar setup - back navigation (use Java listener for reliable click handling)
        toolbar.SetNavigationOnClickListener(new ActionClickListener(() =>
        {
            ParentFragmentManager.PopBackStack();
        }));

        // Toolbar menu
        toolbar.SetOnMenuItemClickListener(new ActionMenuItemClickListener(item =>
        {
            if (item?.ItemId == Resource.Id.action_info)
            {
                if (ViewModel.IsGroup)
                    ShowGroupInfoBottomSheet();
                else
                    ShowContactInfoBottomSheet();
                return true;
            }
            return false;
        }));

        // RecyclerView setup
        _layoutManager = new LinearLayoutManager(Context);
        _layoutManager.StackFromEnd = true;
        recyclerView.SetLayoutManager(_layoutManager);

        _adapter = new MessageAdapter();
        recyclerView.SetAdapter(_adapter);

        // Record button — request RECORD_AUDIO permission, then toggle recording
        var recordButton = view.FindViewById<ImageButton>(Resource.Id.record_button)!;
        recordButton.Click += (s, e) =>
        {
            if (ContextCompat.CheckSelfPermission(RequireContext(), Manifest.Permission.RecordAudio)
                != Permission.Granted)
            {
                RequestPermissions(new[] { Manifest.Permission.RecordAudio }, 1001);
                return;
            }
            ViewModel.ToggleRecordingCommand.Execute().Subscribe().DisposeWith(_disposables);
        };

        // Send button
        sendButton.Click += (s, e) =>
        {
            ViewModel.MessageText = messageInput.Text ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(ViewModel.MessageText))
            {
                ViewModel.SendMessageCommand.Execute().Subscribe().DisposeWith(_disposables);
                messageInput.Text = string.Empty;
            }
        };

        // Bind chat name to toolbar
        ViewModel.WhenAnyValue(x => x.ChatName)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(name => toolbar.Title = name)
            .DisposeWith(_disposables);

        // Observe messages collection
        ViewModel.Messages.CollectionChanged += (s, e) =>
        {
            Activity?.RunOnUiThread(() =>
            {
                _adapter.UpdateItems(ViewModel.Messages.ToList());
                if (ViewModel.Messages.Count > 0)
                {
                    recyclerView.ScrollToPosition(ViewModel.Messages.Count - 1);
                }
            });
        };

        // Bind sending state
        ViewModel.WhenAnyValue(x => x.IsSending)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(sending =>
            {
                sendButton.Enabled = !sending;
            })
            .DisposeWith(_disposables);

        // Clear message input when MessageText is cleared (after send)
        ViewModel.WhenAnyValue(x => x.MessageText)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(text =>
            {
                if (string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(messageInput.Text))
                {
                    messageInput.Text = string.Empty;
                }
            })
            .DisposeWith(_disposables);

        // Initial load
        _adapter.UpdateItems(ViewModel.Messages.ToList());
    }

    private void ShowContactInfoBottomSheet()
    {
        if (Context == null) return;

        var dialogView = LayoutInflater.From(Context)!
            .Inflate(Resource.Layout.bottom_sheet_contact_info, null)!;

        var loading = dialogView.FindViewById<ProgressBar>(Resource.Id.contact_loading)!;
        var displayName = dialogView.FindViewById<TextView>(Resource.Id.contact_display_name)!;
        var username = dialogView.FindViewById<TextView>(Resource.Id.contact_username)!;
        var about = dialogView.FindViewById<TextView>(Resource.Id.contact_about)!;
        var npub = dialogView.FindViewById<TextView>(Resource.Id.contact_npub)!;
        var nip05Label = dialogView.FindViewById<TextView>(Resource.Id.contact_nip05_label)!;
        var nip05 = dialogView.FindViewById<TextView>(Resource.Id.contact_nip05)!;
        var lud16Label = dialogView.FindViewById<TextView>(Resource.Id.contact_lud16_label)!;
        var lud16 = dialogView.FindViewById<TextView>(Resource.Id.contact_lud16)!;

        var dialog = new MaterialAlertDialogBuilder(Context)
            .SetTitle("Contact Info")!
            .SetView(dialogView)!
            .SetPositiveButton("Close", (s, e) => { })!
            .Create()!;

        dialog.Show();

        // Trigger metadata loading
        ViewModel.ToggleMetadataPanelCommand.Execute().Subscribe().DisposeWith(_disposables);

        // Bind metadata properties
        ViewModel.WhenAnyValue(x => x.IsLoadingMetadata)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(isLoading =>
            {
                loading.Visibility = isLoading ? ViewStates.Visible : ViewStates.Gone;
            })
            .DisposeWith(_disposables);

        ViewModel.WhenAnyValue(x => x.MetadataDisplayName)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(name => displayName.Text = name ?? ViewModel.ChatName)
            .DisposeWith(_disposables);

        ViewModel.WhenAnyValue(x => x.MetadataUsername)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(u =>
            {
                username.Text = string.IsNullOrEmpty(u) ? "" : $"@{u}";
                username.Visibility = string.IsNullOrEmpty(u) ? ViewStates.Gone : ViewStates.Visible;
            })
            .DisposeWith(_disposables);

        ViewModel.WhenAnyValue(x => x.MetadataAbout)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(a =>
            {
                about.Text = a ?? "";
                about.Visibility = string.IsNullOrEmpty(a) ? ViewStates.Gone : ViewStates.Visible;
            })
            .DisposeWith(_disposables);

        ViewModel.WhenAnyValue(x => x.MetadataNpub)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(n => npub.Text = n ?? "")
            .DisposeWith(_disposables);

        ViewModel.WhenAnyValue(x => x.MetadataNip05)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(n =>
            {
                nip05.Text = n ?? "";
                nip05.Visibility = string.IsNullOrEmpty(n) ? ViewStates.Gone : ViewStates.Visible;
                nip05Label.Visibility = nip05.Visibility;
            })
            .DisposeWith(_disposables);

        ViewModel.WhenAnyValue(x => x.MetadataLud16)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(l =>
            {
                lud16.Text = l ?? "";
                lud16.Visibility = string.IsNullOrEmpty(l) ? ViewStates.Gone : ViewStates.Visible;
                lud16Label.Visibility = lud16.Visibility;
            })
            .DisposeWith(_disposables);
    }

    private void ShowGroupInfoBottomSheet()
    {
        if (Context == null) return;

        var dialogView = LayoutInflater.From(Context)!
            .Inflate(Resource.Layout.bottom_sheet_group_info, null)!;

        var groupName = dialogView.FindViewById<TextView>(Resource.Id.group_info_name)!;
        var participants = dialogView.FindViewById<TextView>(Resource.Id.group_info_participants)!;
        var inviteButton = dialogView.FindViewById<MaterialButton>(Resource.Id.group_invite_button)!;
        var copyLinkButton = dialogView.FindViewById<MaterialButton>(Resource.Id.group_copy_link_button)!;

        groupName.Text = ViewModel.ChatName;
        participants.Text = $"{ViewModel.ParticipantCount} participants";

        var dialog = new MaterialAlertDialogBuilder(Context)
            .SetView(dialogView)!
            .SetPositiveButton("Close", (s, e) => { })!
            .Create()!;

        dialog.Show();

        inviteButton.Click += (s, e) =>
        {
            dialog.Dismiss();
            ShowInviteMemberDialog();
        };

        copyLinkButton.Click += (s, e) =>
        {
            ViewModel.CopyGroupLinkCommand.Execute().Subscribe().DisposeWith(_disposables);
        };
    }

    private void ShowInviteMemberDialog()
    {
        if (Context == null) return;

        var dialogView = LayoutInflater.From(Context)!
            .Inflate(Resource.Layout.dialog_invite_member, null)!;

        var pubKeyInput = dialogView.FindViewById<TextInputEditText>(Resource.Id.invite_pubkey_input)!;
        var groupLinkSection = dialogView.FindViewById<LinearLayout>(Resource.Id.group_link_section)!;
        var groupLinkText = dialogView.FindViewById<TextView>(Resource.Id.group_invite_link_text)!;
        var copyLinkButton = dialogView.FindViewById<MaterialButton>(Resource.Id.copy_group_link_button)!;
        var errorText = dialogView.FindViewById<TextView>(Resource.Id.invite_error_text)!;
        var successText = dialogView.FindViewById<TextView>(Resource.Id.invite_success_text)!;

        var dialog = new MaterialAlertDialogBuilder(Context)
            .SetTitle("Invite Member")!
            .SetView(dialogView)!
            .SetPositiveButton("Send Invite", (EventHandler<DialogClickEventArgs>)null!)!
            .SetNegativeButton("Close", (s, e) =>
            {
                ViewModel.CloseInviteDialogCommand.Execute().Subscribe().DisposeWith(_disposables);
            })!
            .Create()!;

        dialog.Show();

        dialog.GetButton(-1)!.Click += (s, e) =>
        {
            ViewModel.InvitePublicKey = pubKeyInput.Text ?? string.Empty;
            ViewModel.SendInviteCommand.Execute().Subscribe().DisposeWith(_disposables);
        };

        copyLinkButton.Click += (s, e) =>
        {
            ViewModel.CopyGroupLinkCommand.Execute().Subscribe().DisposeWith(_disposables);
        };

        // Observe invite state
        ViewModel.WhenAnyValue(x => x.InviteError)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(error =>
            {
                errorText.Text = error ?? "";
                errorText.Visibility = string.IsNullOrEmpty(error) ? ViewStates.Gone : ViewStates.Visible;
            })
            .DisposeWith(_disposables);

        ViewModel.WhenAnyValue(x => x.InviteSuccess)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(success =>
            {
                successText.Text = success ?? "";
                successText.Visibility = string.IsNullOrEmpty(success) ? ViewStates.Gone : ViewStates.Visible;
            })
            .DisposeWith(_disposables);

        ViewModel.WhenAnyValue(x => x.GroupInviteLink)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(link =>
            {
                groupLinkText.Text = link ?? "";
                groupLinkSection.Visibility = string.IsNullOrEmpty(link) ? ViewStates.Gone : ViewStates.Visible;
            })
            .DisposeWith(_disposables);

        ViewModel.WhenAnyValue(x => x.ShowInviteDialog)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Where(show => !show)
            .Subscribe(_ => { if (dialog.IsShowing) dialog.Dismiss(); })
            .DisposeWith(_disposables);

        ViewModel.ShowInviteDialogCommand.Execute().Subscribe().DisposeWith(_disposables);
    }

    public override void OnDestroyView()
    {
        _disposables.Dispose();
        _disposables = new CompositeDisposable();
        base.OnDestroyView();
    }
}
