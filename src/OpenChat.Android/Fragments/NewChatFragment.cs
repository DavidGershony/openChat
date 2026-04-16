using Android.OS;
using Android.Views;
using Android.Widget;
using Google.Android.Material.AppBar;
using Google.Android.Material.Button;
using Google.Android.Material.TextField;
using AndroidX.RecyclerView.Widget;
using OpenChat.Android.Adapters;
using OpenChat.Presentation.ViewModels;
using ReactiveUI;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Fragment = AndroidX.Fragment.App.Fragment;

namespace OpenChat.Android.Fragments;

public class NewChatFragment : Fragment
{
    private readonly MainViewModel _mainViewModel;
    private ChatListViewModel ViewModel => _mainViewModel.ChatListViewModel;
    private CompositeDisposable _disposables = new();

    public NewChatFragment(MainViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
    }

    public override View? OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
    {
        return inflater.Inflate(Resource.Layout.fragment_new_chat, container, false);
    }

    public override void OnViewCreated(View view, Bundle? savedInstanceState)
    {
        base.OnViewCreated(view, savedInstanceState);

        var toolbar = view.FindViewById<MaterialToolbar>(Resource.Id.new_chat_toolbar)!;
        var formSection = view.FindViewById<LinearLayout>(Resource.Id.new_chat_form)!;
        var sendingOverlay = view.FindViewById<LinearLayout>(Resource.Id.new_chat_sending_overlay)!;
        var sendingStatusText = view.FindViewById<TextView>(Resource.Id.new_chat_sending_status)!;
        var pubKeyInput = view.FindViewById<TextInputEditText>(Resource.Id.new_chat_pubkey_input)!;
        var nameInput = view.FindViewById<TextInputEditText>(Resource.Id.new_chat_name_input)!;
        var lookupButton = view.FindViewById<MaterialButton>(Resource.Id.lookup_keypackage_button)!;
        var statusText = view.FindViewById<TextView>(Resource.Id.keypackage_status_text)!;
        var errorText = view.FindViewById<TextView>(Resource.Id.new_chat_error)!;
        var relayContainer = view.FindViewById<LinearLayout>(Resource.Id.relay_selection_container)!;
        var createButton = view.FindViewById<MaterialButton>(Resource.Id.create_chat_button)!;

        // Back navigation
        toolbar.NavigationClick += (s, e) =>
        {
            ViewModel.CancelNewChatCommand.Execute().Subscribe().DisposeWith(_disposables);
            ParentFragmentManager.PopBackStack();
        };

        // Create button. For the 1:1 DM flow the user types a single npub in
        // pubKeyInput but the unified ChatListViewModel requires participants to
        // be committed to NewChatParticipants before CreateChatCommand can run.
        // If the collection is empty, commit the typed value first.
        createButton.Click += (s, e) =>
        {
            ViewModel.NewChatName = nameInput.Text ?? string.Empty;
            if (ViewModel.NewChatParticipants.Count == 0)
            {
                ViewModel.NewChatParticipantInput = pubKeyInput.Text ?? string.Empty;
                ViewModel.AddChatParticipantCommand.Execute().Subscribe(_ =>
                {
                    ViewModel.CreateChatCommand.Execute().Subscribe().DisposeWith(_disposables);
                }).DisposeWith(_disposables);
            }
            else
            {
                ViewModel.CreateChatCommand.Execute().Subscribe().DisposeWith(_disposables);
            }
        };

        // Hide manual lookup button — auto-lookup triggers on valid npub input
        lookupButton.Visibility = ViewStates.Gone;

        // Bind text input reactively to ViewModel (triggers validation + auto-lookup)
        pubKeyInput.TextChanged += (s, e) =>
        {
            ViewModel.NewChatParticipantInput = pubKeyInput.Text ?? string.Empty;
        };

        // Show sending overlay while creating
        ViewModel.CreateChatCommand.IsExecuting
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(creating =>
            {
                formSection.Visibility = creating ? ViewStates.Gone : ViewStates.Visible;
                sendingOverlay.Visibility = creating ? ViewStates.Visible : ViewStates.Gone;
                toolbar.Title = creating ? "Sending Invite" : "New Chat";
                createButton.Enabled = !creating;
            })
            .DisposeWith(_disposables);

        // Dynamic progress status text
        ViewModel.WhenAnyValue(x => x.CreateProgress)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(progress =>
            {
                sendingStatusText.Text = progress ?? "Sending invite...";
            })
            .DisposeWith(_disposables);

        // Observe ViewModel state
        ViewModel.WhenAnyValue(x => x.NewChatError)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(error =>
            {
                errorText.Text = error ?? "";
                errorText.Visibility = string.IsNullOrEmpty(error) ? ViewStates.Gone : ViewStates.Visible;
            })
            .DisposeWith(_disposables);

        ViewModel.WhenAnyValue(x => x.KeyPackageStatus)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(status =>
            {
                statusText.Text = status ?? "";
                statusText.Visibility = string.IsNullOrEmpty(status) ? ViewStates.Gone : ViewStates.Visible;
            })
            .DisposeWith(_disposables);

        // Disable Create button until KeyPackage is found and relays selected
        ViewModel.CreateChatCommand.CanExecute
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(canCreate =>
            {
                createButton.Enabled = canCreate;
            })
            .DisposeWith(_disposables);

        // Show validation error on input field
        var pubKeyLayout = view.FindViewById<TextInputLayout>(Resource.Id.new_chat_pubkey_layout);
        ViewModel.WhenAnyValue(x => x.NewChatError)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(error =>
            {
                if (pubKeyLayout != null)
                {
                    pubKeyLayout.Error = string.IsNullOrEmpty(error) ? null : error;
                }
            })
            .DisposeWith(_disposables);

        ViewModel.WhenAnyValue(x => x.IsLookingUpKeyPackages)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(loading =>
            {
                statusText.Text = loading ? "Looking up KeyPackage..." : statusText.Text;
            })
            .DisposeWith(_disposables);

        // Contacts picker: populated from NIP-02 follow list cached in VM
        var contactsHeader = view.FindViewById<TextView>(Resource.Id.contacts_header)!;
        var followingList = view.FindViewById<RecyclerView>(Resource.Id.following_list)!;
        var contactAdapter = new FollowContactAdapter();
        followingList.SetLayoutManager(new LinearLayoutManager(Context));
        followingList.SetAdapter(contactAdapter);
        contactAdapter.ItemClick += (s, contact) =>
        {
            pubKeyInput.Text = contact.Npub;
            pubKeyInput.SetSelection(contact.Npub.Length);
        };

        void RefreshContactList()
        {
            var visible = ViewModel.Following.Where(f => f.IsVisible).ToList();
            contactAdapter.UpdateItems(visible);
            var hasAny = ViewModel.Following.Count > 0;
            contactsHeader.Visibility = hasAny ? ViewStates.Visible : ViewStates.Gone;
            followingList.Visibility = hasAny ? ViewStates.Visible : ViewStates.Gone;
        }

        RefreshContactList();
        ViewModel.Following.CollectionChanged += (s, e) =>
            Activity?.RunOnUiThread(RefreshContactList);
        ViewModel.WhenAnyValue(x => x.NewChatParticipantInput)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => RefreshContactList())
            .DisposeWith(_disposables);

        // Set flag before subscribing to avoid immediate dismiss
        ViewModel.ShowNewChatDialog = true;

        // Populate relay checkboxes
        relayContainer.RemoveAllViews();
        foreach (var relay in ViewModel.SelectableRelays)
        {
            var checkBox = new CheckBox(Context)
            {
                Text = relay.Url,
                Checked = relay.IsSelected,
                TextSize = 12f
            };
            checkBox.SetTypeface(global::Android.Graphics.Typeface.Monospace, global::Android.Graphics.TypefaceStyle.Normal);
            checkBox.CheckedChange += (s, e) => relay.IsSelected = e.IsChecked;
            relayContainer.AddView(checkBox);
        }

        // Auto-navigate back on success
        ViewModel.WhenAnyValue(x => x.ShowNewChatDialog)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Where(show => !show)
            .Subscribe(_ => ParentFragmentManager.PopBackStack())
            .DisposeWith(_disposables);
    }

    public override void OnDestroyView()
    {
        _disposables.Dispose();
        _disposables = new CompositeDisposable();
        base.OnDestroyView();
    }
}
