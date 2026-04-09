using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.Activity.Result;
using AndroidX.Activity.Result.Contract;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using AndroidX.RecyclerView.Widget;
using Google.Android.Material.AppBar;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.FloatingActionButton;
using Google.Android.Material.TextField;
using Microsoft.Extensions.Logging;
using OpenChat.Android.Adapters;
using OpenChat.Core.Logging;
using OpenChat.Presentation.ViewModels;
using ReactiveUI;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Linq;
using Fragment = AndroidX.Fragment.App.Fragment;

namespace OpenChat.Android.Fragments;

public class ChatFragment : Fragment
{
    private readonly MainViewModel _mainViewModel;
    private readonly ILogger<ChatFragment> _logger;
    private ChatViewModel ViewModel => _mainViewModel.ChatViewModel;
    private CompositeDisposable _disposables = new();
    private MessageAdapter? _adapter;
    private LinearLayoutManager? _layoutManager;
    private ActivityResultLauncher? _filePickerLauncher;
    private TaskCompletionSource<(byte[] Data, string FileName, string MimeType)?>? _filePickerTcs;
    private string _pickerMimeType = "*/*";
    private ImageButton? _attachButton;

    public ChatFragment(MainViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
        _logger = LoggingConfiguration.CreateLogger<ChatFragment>();
    }

    public override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Register file picker launcher
        _filePickerLauncher = RegisterForActivityResult(
            new ActivityResultContracts.GetContent(),
            new FilePickerCallback(this));

        // Set the file picker function on the ViewModel
        ChatViewModel.FilePickerFunc = PickFileAsync;
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
        var messageInputLayout = view.FindViewById<TextInputLayout>(Resource.Id.message_input_layout)!;
        var sendButton = view.FindViewById<FloatingActionButton>(Resource.Id.send_button)!;
        var uploadStatus = view.FindViewById<TextView>(Resource.Id.upload_status)!;
        var recordingIndicator = view.FindViewById<LinearLayout>(Resource.Id.recording_indicator)!;
        var recordingDuration = view.FindViewById<TextView>(Resource.Id.recording_duration)!;
        var cancelRecordingButton = view.FindViewById<ImageButton>(Resource.Id.cancel_recording_button)!;
        var attachButton = view.FindViewById<ImageButton>(Resource.Id.attach_button)!;

        // Toolbar setup - back navigation
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

        // Load older messages when scrolling to the top
        recyclerView.AddOnScrollListener(new LoadOlderScrollListener(_layoutManager, () =>
        {
            if (ViewModel.CanLoadOlder && !ViewModel.IsLoadingOlder)
            {
                _logger.LogInformation("Scroll reached top, loading older messages");
                ViewModel.LoadOlderMessagesCommand.Execute().Subscribe().DisposeWith(_disposables);
            }
        }));

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

        // Cancel recording button
        cancelRecordingButton.Click += (s, e) =>
        {
            ViewModel.CancelRecordingCommand.Execute().Subscribe().DisposeWith(_disposables);
        };

        // Attach file button — request media permissions on Android 13+, then show picker menu
        _attachButton = attachButton;
        attachButton.Click += (s, e) =>
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
            {
                var needed = new List<string>();
                if (ContextCompat.CheckSelfPermission(RequireContext(), Manifest.Permission.ReadMediaImages) != Permission.Granted)
                    needed.Add(Manifest.Permission.ReadMediaImages);
                if (ContextCompat.CheckSelfPermission(RequireContext(), Manifest.Permission.ReadMediaVideo) != Permission.Granted)
                    needed.Add(Manifest.Permission.ReadMediaVideo);
                if (needed.Count > 0)
                {
                    RequestPermissions(needed.ToArray(), 1002);
                    return;
                }
            }
            else if (ContextCompat.CheckSelfPermission(RequireContext(), Manifest.Permission.ReadExternalStorage) != Permission.Granted)
            {
                RequestPermissions(new[] { Manifest.Permission.ReadExternalStorage }, 1002);
                return;
            }
            ShowAttachMenu(attachButton);
        };

        // Reply preview bar
        var replyPreview = view.FindViewById<LinearLayout>(Resource.Id.reply_preview)!;
        var replyPreviewSender = view.FindViewById<TextView>(Resource.Id.reply_preview_sender)!;
        var replyPreviewContent = view.FindViewById<TextView>(Resource.Id.reply_preview_content)!;
        var cancelReplyButton = view.FindViewById<ImageButton>(Resource.Id.cancel_reply_button)!;

        cancelReplyButton.Click += (s, e) =>
        {
            ViewModel.CancelReplyCommand.Execute().Subscribe().DisposeWith(_disposables);
        };

        // Wire up reply from long-press menu
        MessageAdapter.OnReplyRequested = msgVm =>
        {
            ViewModel.SetReplyTo(msgVm.Message);
            messageInput.RequestFocus();
        };

        // Bind reply preview visibility
        ViewModel.WhenAnyValue(x => x.ReplyingToMessage)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(msg =>
            {
                replyPreview.Visibility = msg != null ? ViewStates.Visible : ViewStates.Gone;
            })
            .DisposeWith(_disposables);

        ViewModel.WhenAnyValue(x => x.ReplyPreviewSender)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(sender => replyPreviewSender.Text = sender ?? "")
            .DisposeWith(_disposables);

        ViewModel.WhenAnyValue(x => x.ReplyPreviewText)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(text => replyPreviewContent.Text = text ?? "")
            .DisposeWith(_disposables);

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

        // Bind upload status banner
        ViewModel.WhenAnyValue(x => x.UploadStatus)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(status =>
            {
                uploadStatus.Text = status ?? "";
                uploadStatus.Visibility = string.IsNullOrEmpty(status) ? ViewStates.Gone : ViewStates.Visible;
            })
            .DisposeWith(_disposables);

        // Bind recording state
        ViewModel.WhenAnyValue(x => x.IsRecording)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(recording =>
            {
                recordingIndicator.Visibility = recording ? ViewStates.Visible : ViewStates.Gone;
                messageInputLayout.Visibility = recording ? ViewStates.Gone : ViewStates.Visible;
                sendButton.Visibility = recording ? ViewStates.Gone : ViewStates.Visible;
            })
            .DisposeWith(_disposables);

        // Bind recording duration
        ViewModel.WhenAnyValue(x => x.RecordingDuration)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(dur =>
            {
                recordingDuration.Text = dur;
            })
            .DisposeWith(_disposables);

        // Bind MIP-04 enabled state for attach/record button visibility
        ViewModel.WhenAnyValue(x => x.IsMip04Enabled)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(enabled =>
            {
                attachButton.Visibility = enabled ? ViewStates.Visible : ViewStates.Gone;
                recordButton.Visibility = enabled ? ViewStates.Visible : ViewStates.Gone;
            })
            .DisposeWith(_disposables);

        // Initial load
        _adapter.UpdateItems(ViewModel.Messages.ToList());
    }

    private void ShowAttachMenu(View anchor)
    {
        var popup = new PopupMenu(RequireContext(), anchor);
        popup.Menu!.Add(0, 1, 0, "Photo");
        popup.Menu!.Add(0, 2, 0, "File");
        popup.MenuItemClick += (sender, args) =>
        {
            _pickerMimeType = args.Item!.ItemId == 1 ? "image/*" : "*/*";
            ViewModel.AttachFileCommand.Execute().Subscribe().DisposeWith(_disposables);
        };
        popup.Show();
    }

    private Task<(byte[] Data, string FileName, string MimeType)?> PickFileAsync()
    {
        _filePickerTcs = new TaskCompletionSource<(byte[] Data, string FileName, string MimeType)?>();
        try
        {
            _filePickerLauncher?.Launch(_pickerMimeType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch file picker");
            _filePickerTcs.TrySetResult(null);
        }
        return _filePickerTcs.Task;
    }

    private void OnFilePickerResult(global::Android.Net.Uri? uri)
    {
        if (uri == null || Context == null)
        {
            _filePickerTcs?.TrySetResult(null);
            return;
        }

        try
        {
            var contentResolver = Context.ContentResolver!;
            var mimeType = contentResolver.GetType(uri) ?? "application/octet-stream";
            var fileName = GetFileName(uri) ?? "image";

            using var stream = contentResolver.OpenInputStream(uri);
            if (stream == null)
            {
                _filePickerTcs?.TrySetResult(null);
                return;
            }

            using var memStream = new MemoryStream();
            stream.CopyTo(memStream);
            var data = memStream.ToArray();

            _logger.LogInformation("File picked: {Name}, {Size} bytes, {Mime}", fileName, data.Length, mimeType);
            _filePickerTcs?.TrySetResult((data, fileName, mimeType));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read picked file");
            _filePickerTcs?.TrySetResult(null);
        }
    }

    private string? GetFileName(global::Android.Net.Uri uri)
    {
        if (Context == null) return null;
        try
        {
            using var cursor = Context.ContentResolver?.Query(uri, null, null, null, null);
            if (cursor != null && cursor.MoveToFirst())
            {
                var nameIndex = cursor.GetColumnIndex(global::Android.Provider.OpenableColumns.DisplayName);
                if (nameIndex >= 0)
                    return cursor.GetString(nameIndex);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get file name from URI");
        }
        return null;
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);

        if (grantResults.Length == 0 || grantResults.Any(r => r != Permission.Granted))
        {
            _logger.LogWarning("Permission denied for request code {Code}", requestCode);
            return;
        }

        switch (requestCode)
        {
            case 1001: // RECORD_AUDIO
                _logger.LogInformation("RECORD_AUDIO permission granted, starting recording");
                ViewModel.ToggleRecordingCommand.Execute().Subscribe().DisposeWith(_disposables);
                break;
            case 1002: // READ_MEDIA / READ_EXTERNAL_STORAGE
                _logger.LogInformation("Media read permission granted, showing attach menu");
                if (_attachButton != null)
                    ShowAttachMenu(_attachButton);
                break;
        }
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
        var membersRecycler = dialogView.FindViewById<RecyclerView>(Resource.Id.group_members_recycler)!;

        groupName.Text = ViewModel.ChatName;
        participants.Text = $"{ViewModel.ParticipantCount} participants";

        // Set up member list
        var memberAdapter = new Adapters.GroupMemberAdapter();
        membersRecycler.SetLayoutManager(new LinearLayoutManager(Context));
        membersRecycler.SetAdapter(memberAdapter);

        // Hide invite button for non-admins
        inviteButton.Visibility = ViewModel.IsCurrentUserAdmin ? ViewStates.Visible : ViewStates.Gone;

        // Load members and update adapter
        if (ViewModel.GroupMembers.Count > 0)
        {
            memberAdapter.UpdateItems(ViewModel.GroupMembers.ToList());
        }

        // Trigger member loading if not already loaded
        ViewModel.ShowChatInfoCommand.Execute().Subscribe(_ =>
        {
            Activity?.RunOnUiThread(() =>
            {
                memberAdapter.UpdateItems(ViewModel.GroupMembers.ToList());
            });
        });

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

    /// <summary>
    /// Callback for the Android file picker activity result.
    /// </summary>
    private class FilePickerCallback : Java.Lang.Object, IActivityResultCallback
    {
        private readonly ChatFragment _fragment;

        public FilePickerCallback(ChatFragment fragment) => _fragment = fragment;

        public void OnActivityResult(Java.Lang.Object? result)
        {
            var uri = result as global::Android.Net.Uri;
            _fragment.OnFilePickerResult(uri);
        }
    }

    /// <summary>
    /// Triggers a callback when the user scrolls to the top of the list.
    /// </summary>
    private class LoadOlderScrollListener : RecyclerView.OnScrollListener
    {
        private readonly LinearLayoutManager _layoutManager;
        private readonly Action _onReachedTop;

        public LoadOlderScrollListener(LinearLayoutManager layoutManager, Action onReachedTop)
        {
            _layoutManager = layoutManager;
            _onReachedTop = onReachedTop;
        }

        public override void OnScrolled(RecyclerView recyclerView, int dx, int dy)
        {
            // Only trigger when scrolling upward
            if (dy >= 0) return;

            if (_layoutManager.FindFirstVisibleItemPosition() == 0)
            {
                _onReachedTop();
            }
        }
    }
}
