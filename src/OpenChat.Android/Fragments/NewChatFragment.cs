using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using Google.Android.Material.AppBar;
using Google.Android.Material.Button;
using Google.Android.Material.Chip;
using Google.Android.Material.TextField;
using OpenChat.Android.Adapters;
using OpenChat.Presentation.ViewModels;
using ReactiveUI;
using System.Collections.Specialized;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Fragment = AndroidX.Fragment.App.Fragment;

namespace OpenChat.Android.Fragments;

public class NewChatFragment : Fragment
{
    private readonly MainViewModel _mainViewModel;
    private ChatListViewModel ViewModel => _mainViewModel.ChatListViewModel;
    private CompositeDisposable _disposables = new();
    private ChipGroup? _chipGroup;
    private NotifyCollectionChangedEventHandler? _participantsChangedHandler;

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
        var nameInput = view.FindViewById<TextInputEditText>(Resource.Id.new_chat_name_input)!;
        var descInput = view.FindViewById<TextInputEditText>(Resource.Id.new_chat_desc_input)!;
        var participantInput = view.FindViewById<TextInputEditText>(Resource.Id.new_chat_participant_input)!;
        var addParticipantButton = view.FindViewById<MaterialButton>(Resource.Id.new_chat_add_participant_button)!;
        _chipGroup = view.FindViewById<ChipGroup>(Resource.Id.new_chat_participant_chips)!;
        var contactsHeader = view.FindViewById<TextView>(Resource.Id.contacts_header)!;
        var contactsList = view.FindViewById<RecyclerView>(Resource.Id.following_list)!;
        var lookupButton = view.FindViewById<MaterialButton>(Resource.Id.lookup_keypackages_button)!;
        var statusText = view.FindViewById<TextView>(Resource.Id.keypackage_status_text)!;
        var errorText = view.FindViewById<TextView>(Resource.Id.new_chat_error)!;
        var relayContainer = view.FindViewById<LinearLayout>(Resource.Id.relay_selection_container)!;
        var createButton = view.FindViewById<MaterialButton>(Resource.Id.create_chat_button)!;

        toolbar.NavigationClick += (s, e) =>
        {
            ViewModel.CancelNewChatCommand.Execute().Subscribe().DisposeWith(_disposables);
            ParentFragmentManager.PopBackStack();
        };

        // Contacts picker
        var contactsAdapter = new FollowContactAdapter();
        contactsList.SetLayoutManager(new LinearLayoutManager(Context));
        contactsList.SetAdapter(contactsAdapter);
        contactsAdapter.ItemClick += (s, contact) =>
        {
            ViewModel.AddContactToChatCommand.Execute(contact.PublicKeyHex).Subscribe().DisposeWith(_disposables);
        };
        contactsAdapter.UpdateItems(ViewModel.Following);
        var showContacts = ViewModel.Following.Count > 0;
        contactsList.Visibility = showContacts ? ViewStates.Visible : ViewStates.Gone;
        contactsHeader.Visibility = showContacts ? ViewStates.Visible : ViewStates.Gone;
        ViewModel.Following.CollectionChanged += (s, e) =>
        {
            Activity?.RunOnUiThread(() =>
            {
                contactsAdapter.UpdateItems(ViewModel.Following);
                var show = ViewModel.Following.Count > 0;
                contactsList.Visibility = show ? ViewStates.Visible : ViewStates.Gone;
                contactsHeader.Visibility = show ? ViewStates.Visible : ViewStates.Gone;
            });
        };

        // Add participant — button or IME action
        void AddFromInput()
        {
            ViewModel.NewChatParticipantInput = participantInput.Text ?? string.Empty;
            ViewModel.AddChatParticipantCommand.Execute().Subscribe().DisposeWith(_disposables);
            participantInput.Text = string.Empty;
        }
        addParticipantButton.Click += (s, e) => AddFromInput();
        participantInput.EditorAction += (s, e) =>
        {
            if (e.ActionId == global::Android.Views.InputMethods.ImeAction.Done ||
                e.ActionId == global::Android.Views.InputMethods.ImeAction.Next ||
                e.Event?.KeyCode == global::Android.Views.Keycode.Enter)
            {
                AddFromInput();
                e.Handled = true;
            }
        };

        // Render chips mirroring the ViewModel collection
        RenderChips();
        _participantsChangedHandler = (s, e) => Activity?.RunOnUiThread(RenderChips);
        ViewModel.NewChatParticipants.CollectionChanged += _participantsChangedHandler;

        createButton.Click += (s, e) =>
        {
            ViewModel.NewChatName = nameInput.Text ?? string.Empty;
            ViewModel.NewChatDescription = descInput.Text ?? string.Empty;
            ViewModel.CreateChatCommand.Execute().Subscribe().DisposeWith(_disposables);
        };

        lookupButton.Click += (s, e) =>
        {
            ViewModel.LookupKeyPackagesCommand.Execute().Subscribe().DisposeWith(_disposables);
        };

        // Show sending overlay while creating
        ViewModel.CreateChatCommand.IsExecuting
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(creating =>
            {
                formSection.Visibility = creating ? ViewStates.Gone : ViewStates.Visible;
                sendingOverlay.Visibility = creating ? ViewStates.Visible : ViewStates.Gone;
                toolbar.Title = creating ? "Creating..." : "New Chat";
                createButton.Enabled = !creating;
            })
            .DisposeWith(_disposables);

        ViewModel.WhenAnyValue(x => x.CreateProgress)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(progress =>
            {
                sendingStatusText.Text = progress ?? "Creating chat...";
            })
            .DisposeWith(_disposables);

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

        ViewModel.WhenAnyValue(x => x.IsLookingUpKeyPackages)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(loading =>
            {
                lookupButton.Enabled = !loading;
                lookupButton.Text = loading ? "Looking up..." : "Lookup KeyPackages";
            })
            .DisposeWith(_disposables);

        ViewModel.CreateChatCommand.CanExecute
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(canCreate => createButton.Enabled = canCreate)
            .DisposeWith(_disposables);

        ViewModel.ShowNewChatDialog = true;

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

        ViewModel.WhenAnyValue(x => x.ShowNewChatDialog)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Where(show => !show)
            .Subscribe(_ => ParentFragmentManager.PopBackStack())
            .DisposeWith(_disposables);
    }

    private void RenderChips()
    {
        if (_chipGroup == null || Context == null) return;
        _chipGroup.RemoveAllViews();
        foreach (var p in ViewModel.NewChatParticipants.ToList())
        {
            var chip = new Chip(Context)
            {
                Text = p.ShownName,
                Checkable = false,
                CloseIconVisible = true
            };
            var captured = p;
            chip.SetOnCloseIconClickListener(new ActionClickListener(() =>
                ViewModel.RemoveChatParticipantCommand.Execute(captured).Subscribe().DisposeWith(_disposables)));
            _chipGroup.AddView(chip);
        }
    }

    public override void OnDestroyView()
    {
        if (_participantsChangedHandler != null)
        {
            ViewModel.NewChatParticipants.CollectionChanged -= _participantsChangedHandler;
            _participantsChangedHandler = null;
        }
        _chipGroup = null;
        _disposables.Dispose();
        _disposables = new CompositeDisposable();
        base.OnDestroyView();
    }
}
