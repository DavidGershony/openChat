using Android.OS;
using Android.Views;
using Android.Widget;
using Google.Android.Material.AppBar;
using Google.Android.Material.Button;
using Google.Android.Material.TextField;
using OpenChat.Presentation.ViewModels;
using ReactiveUI;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Fragment = AndroidX.Fragment.App.Fragment;

namespace OpenChat.Android.Fragments;

public class AddBotFragment : Fragment
{
    private readonly MainViewModel _mainViewModel;
    private ChatListViewModel ViewModel => _mainViewModel.ChatListViewModel;
    private CompositeDisposable _disposables = new();

    public AddBotFragment(MainViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
    }

    public override View? OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
    {
        return inflater.Inflate(Resource.Layout.fragment_add_bot, container, false);
    }

    public override void OnViewCreated(View view, Bundle? savedInstanceState)
    {
        base.OnViewCreated(view, savedInstanceState);

        var toolbar = view.FindViewById<MaterialToolbar>(Resource.Id.add_bot_toolbar)!;
        var npubInput = view.FindViewById<TextInputEditText>(Resource.Id.add_bot_npub_input)!;
        var errorText = view.FindViewById<TextView>(Resource.Id.add_bot_error)!;
        var addButton = view.FindViewById<MaterialButton>(Resource.Id.add_bot_button)!;
        var sendingOverlay = view.FindViewById<LinearLayout>(Resource.Id.add_bot_sending_overlay)!;
        var formScroll = view.FindViewById<ScrollView>(Resource.Id.add_bot_form_scroll)!;

        // Relay selection views
        var relayModeGroup = view.FindViewById<RadioGroup>(Resource.Id.relay_mode_group)!;
        var relayModeNip65 = view.FindViewById<RadioButton>(Resource.Id.relay_mode_nip65)!;
        var relayModeList = view.FindViewById<RadioButton>(Resource.Id.relay_mode_list)!;
        var relayModeManual = view.FindViewById<RadioButton>(Resource.Id.relay_mode_manual)!;

        var nip65Panel = view.FindViewById<LinearLayout>(Resource.Id.nip65_panel)!;
        var lookupNip65Button = view.FindViewById<MaterialButton>(Resource.Id.lookup_nip65_button)!;
        var nip65Progress = view.FindViewById<ProgressBar>(Resource.Id.nip65_progress)!;
        var nip65Status = view.FindViewById<TextView>(Resource.Id.nip65_status)!;
        var nip65RelayList = view.FindViewById<LinearLayout>(Resource.Id.nip65_relay_list)!;

        var connectedRelaysPanel = view.FindViewById<LinearLayout>(Resource.Id.connected_relays_panel)!;
        var connectedRelayList = view.FindViewById<LinearLayout>(Resource.Id.connected_relay_list)!;
        var noRelaysText = view.FindViewById<TextView>(Resource.Id.no_relays_text)!;

        var manualRelayPanel = view.FindViewById<LinearLayout>(Resource.Id.manual_relay_panel)!;
        var manualRelayInput = view.FindViewById<TextInputEditText>(Resource.Id.manual_relay_input)!;

        // Back navigation
        toolbar.NavigationClick += (s, e) =>
        {
            ViewModel.CancelAddBotCommand.Execute().Subscribe().DisposeWith(_disposables);
            ParentFragmentManager.PopBackStack();
        };

        // Relay mode switching
        relayModeGroup.CheckedChange += (s, e) =>
        {
            ViewModel.BotRelayModeNip65 = e.CheckedId == Resource.Id.relay_mode_nip65;
            ViewModel.BotRelayModeList = e.CheckedId == Resource.Id.relay_mode_list;
            ViewModel.BotRelayModeManual = e.CheckedId == Resource.Id.relay_mode_manual;

            nip65Panel.Visibility = ViewModel.BotRelayModeNip65 ? ViewStates.Visible : ViewStates.Gone;
            connectedRelaysPanel.Visibility = ViewModel.BotRelayModeList ? ViewStates.Visible : ViewStates.Gone;
            manualRelayPanel.Visibility = ViewModel.BotRelayModeManual ? ViewStates.Visible : ViewStates.Gone;
        };

        // NIP-65 lookup
        lookupNip65Button.Click += (s, e) =>
        {
            ViewModel.BotNpub = npubInput.Text ?? string.Empty;
            ViewModel.LookupBotNip65Command.Execute().Subscribe().DisposeWith(_disposables);
        };

        // Manual relay sync on focus loss
        manualRelayInput.FocusChange += (s, e) =>
        {
            if (!e.HasFocus && !string.IsNullOrWhiteSpace(manualRelayInput.Text))
                ViewModel.BotManualRelay = manualRelayInput.Text!.Trim();
        };

        // Link button
        addButton.Click += (s, e) =>
        {
            ViewModel.BotNpub = npubInput.Text ?? string.Empty;
            if (ViewModel.BotRelayModeManual && !string.IsNullOrWhiteSpace(manualRelayInput.Text))
                ViewModel.BotManualRelay = manualRelayInput.Text!.Trim();
            ViewModel.CreateBotChatCommand.Execute().Subscribe().DisposeWith(_disposables);
        };

        // === Bindings ===

        // Error state
        ViewModel.WhenAnyValue(x => x.AddBotError)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(error =>
            {
                errorText.Text = error ?? "";
                errorText.Visibility = string.IsNullOrEmpty(error) ? ViewStates.Gone : ViewStates.Visible;
            })
            .DisposeWith(_disposables);

        // Sending overlay
        ViewModel.CreateBotChatCommand.IsExecuting
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(creating =>
            {
                formScroll.Visibility = creating ? ViewStates.Gone : ViewStates.Visible;
                sendingOverlay.Visibility = creating ? ViewStates.Visible : ViewStates.Gone;
                toolbar.Title = creating ? "Linking..." : "Link Device";
                addButton.Enabled = !creating;
            })
            .DisposeWith(_disposables);

        // NIP-65 fetching state
        ViewModel.WhenAnyValue(x => x.IsFetchingNip65)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(fetching =>
            {
                nip65Progress.Visibility = fetching ? ViewStates.Visible : ViewStates.Gone;
                lookupNip65Button.Enabled = !fetching;
                lookupNip65Button.Text = fetching ? "Looking up..." : "Look up relays";
            })
            .DisposeWith(_disposables);

        // NIP-65 status
        ViewModel.WhenAnyValue(x => x.Nip65Status)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(status =>
            {
                nip65Status.Text = status ?? "";
                nip65Status.Visibility = string.IsNullOrEmpty(status) ? ViewStates.Gone : ViewStates.Visible;
            })
            .DisposeWith(_disposables);

        // NIP-65 relay checkboxes
        ViewModel.BotNip65Relays.CollectionChanged += (s, e) =>
        {
            Activity?.RunOnUiThread(() => RebuildRelayCheckboxes(nip65RelayList, ViewModel.BotNip65Relays));
        };

        // Connected relay checkboxes
        ViewModel.BotAvailableRelays.CollectionChanged += (s, e) =>
        {
            Activity?.RunOnUiThread(() =>
            {
                RebuildRelayCheckboxes(connectedRelayList, ViewModel.BotAvailableRelays);
                noRelaysText.Visibility = ViewModel.BotAvailableRelays.Count == 0 ? ViewStates.Visible : ViewStates.Gone;
            });
        };

        // Set flag and init relay lists
        ViewModel.ShowAddBotDialog = true;

        // Populate connected relays now (AddBotCommand already ran from ViewModel)
        RebuildRelayCheckboxes(connectedRelayList, ViewModel.BotAvailableRelays);
        noRelaysText.Visibility = ViewModel.BotAvailableRelays.Count == 0 ? ViewStates.Visible : ViewStates.Gone;

        // Auto-navigate back on success
        ViewModel.WhenAnyValue(x => x.ShowAddBotDialog)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Where(show => !show)
            .Subscribe(_ => ParentFragmentManager.PopBackStack())
            .DisposeWith(_disposables);
    }

    private void RebuildRelayCheckboxes(LinearLayout container, System.Collections.ObjectModel.ObservableCollection<RelayCheckItem> items)
    {
        container.RemoveAllViews();
        foreach (var item in items)
        {
            var cb = new CheckBox(Context)
            {
                Text = item.Url,
                Checked = item.IsChecked,
            };
            cb.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 13);
            cb.CheckedChange += (s, e) => item.IsChecked = e.IsChecked;
            container.AddView(cb);
        }
    }

    public override void OnDestroyView()
    {
        _disposables.Dispose();
        _disposables = new CompositeDisposable();
        base.OnDestroyView();
    }
}
