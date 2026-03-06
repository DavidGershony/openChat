using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using Google.Android.Material.AppBar;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.TextField;
using OpenChat.Android.Adapters;
using OpenChat.Presentation.ViewModels;
using ReactiveUI;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Fragment = AndroidX.Fragment.App.Fragment;

namespace OpenChat.Android.Fragments;

public class SettingsFragment : Fragment
{
    private readonly MainViewModel _mainViewModel;
    private SettingsViewModel ViewModel => _mainViewModel.SettingsViewModel;
    private CompositeDisposable _disposables = new();
    private RelayAdapter? _relayAdapter;

    public SettingsFragment(MainViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
    }

    public override View? OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
    {
        return inflater.Inflate(Resource.Layout.fragment_settings, container, false);
    }

    public override void OnViewCreated(View view, Bundle? savedInstanceState)
    {
        base.OnViewCreated(view, savedInstanceState);

        var toolbar = view.FindViewById<MaterialToolbar>(Resource.Id.settings_toolbar)!;
        var displayNameInput = view.FindViewById<TextInputEditText>(Resource.Id.display_name_input)!;
        var usernameInput = view.FindViewById<TextInputEditText>(Resource.Id.username_input)!;
        var aboutInput = view.FindViewById<TextInputEditText>(Resource.Id.about_input)!;
        var saveProfileButton = view.FindViewById<MaterialButton>(Resource.Id.save_profile_button)!;
        var npubText = view.FindViewById<TextView>(Resource.Id.npub_text)!;
        var copyNpubButton = view.FindViewById<ImageButton>(Resource.Id.copy_npub_button)!;

        // Relay views
        var relayRecycler = view.FindViewById<RecyclerView>(Resource.Id.relay_recycler)!;
        var newRelayInput = view.FindViewById<TextInputEditText>(Resource.Id.new_relay_input)!;
        var addRelayButton = view.FindViewById<MaterialButton>(Resource.Id.add_relay_button)!;

        // Key Package views
        var publishButton = view.FindViewById<MaterialButton>(Resource.Id.publish_keypackage_button)!;
        var keypackageProgress = view.FindViewById<ProgressBar>(Resource.Id.keypackage_progress)!;
        var keypackageStatus = view.FindViewById<TextView>(Resource.Id.keypackage_status)!;

        // Developer views
        var viewLogsButton = view.FindViewById<MaterialButton>(Resource.Id.view_logs_button)!;

        // Toolbar back navigation (use Java listener for reliable click handling)
        toolbar.SetNavigationOnClickListener(new ActionClickListener(() =>
        {
            ParentFragmentManager.PopBackStack();
        }));

        // Save profile button
        saveProfileButton.Click += (s, e) =>
        {
            ViewModel.DisplayName = displayNameInput.Text;
            ViewModel.Username = usernameInput.Text;
            ViewModel.About = aboutInput.Text;
            ViewModel.SaveProfileCommand.Execute().Subscribe().DisposeWith(_disposables);
        };

        // Copy npub
        copyNpubButton.Click += (s, e) =>
        {
            CopyToClipboard("npub", ViewModel.Npub);
        };

        // Relay management
        _relayAdapter = new RelayAdapter();
        relayRecycler.SetLayoutManager(new LinearLayoutManager(Context));
        relayRecycler.SetAdapter(_relayAdapter);

        _relayAdapter.RemoveClick += (s, relay) =>
        {
            ViewModel.RemoveRelayCommand.Execute(relay).Subscribe().DisposeWith(_disposables);
        };

        addRelayButton.Click += (s, e) =>
        {
            ViewModel.NewRelayUrl = newRelayInput.Text ?? string.Empty;
            ViewModel.AddRelayCommand.Execute().Subscribe().DisposeWith(_disposables);
            newRelayInput.Text = string.Empty;
        };

        // Key Package
        publishButton.Click += (s, e) =>
        {
            ViewModel.PublishKeyPackageCommand.Execute().Subscribe().DisposeWith(_disposables);
        };

        // View Logs
        viewLogsButton.Click += (s, e) =>
        {
            ViewModel.ViewLogsCommand.Execute().Subscribe().DisposeWith(_disposables);
        };

        // === Bindings ===

        // Profile fields
        ViewModel.WhenAnyValue(x => x.DisplayName)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(name =>
            {
                if (displayNameInput.Text != name)
                    displayNameInput.Text = name;
            })
            .DisposeWith(_disposables);

        ViewModel.WhenAnyValue(x => x.Username)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(username =>
            {
                if (usernameInput.Text != username)
                    usernameInput.Text = username;
            })
            .DisposeWith(_disposables);

        ViewModel.WhenAnyValue(x => x.About)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(about =>
            {
                if (aboutInput.Text != about)
                    aboutInput.Text = about;
            })
            .DisposeWith(_disposables);

        ViewModel.WhenAnyValue(x => x.Npub)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(npub => npubText.Text = npub ?? "No key available")
            .DisposeWith(_disposables);

        // Relay collection
        ViewModel.Relays.CollectionChanged += (s, e) =>
        {
            Activity?.RunOnUiThread(() =>
            {
                _relayAdapter.UpdateItems(ViewModel.Relays.ToList());
            });
        };

        // Key Package state
        ViewModel.WhenAnyValue(x => x.IsPublishingKeyPackage)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(publishing =>
            {
                publishButton.Enabled = !publishing;
                publishButton.Text = publishing ? "Publishing..." : "Publish Key Package";
                keypackageProgress.Visibility = publishing ? ViewStates.Visible : ViewStates.Gone;
            })
            .DisposeWith(_disposables);

        ViewModel.WhenAnyValue(x => x.KeyPackageStatus)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(status =>
            {
                keypackageStatus.Text = status ?? "";
                keypackageStatus.Visibility = string.IsNullOrEmpty(status) ? ViewStates.Gone : ViewStates.Visible;
            })
            .DisposeWith(_disposables);

        ViewModel.WhenAnyValue(x => x.KeyPackageSuccess)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(success =>
            {
                keypackageStatus.SetTextColor(success
                    ? global::Android.Graphics.Color.ParseColor("#FF10B981")
                    : global::Android.Graphics.Color.ParseColor("#FFEF4444"));
            })
            .DisposeWith(_disposables);

        // Log viewer
        ViewModel.WhenAnyValue(x => x.ShowLogViewer)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Where(show => show)
            .Subscribe(_ => ShowLogViewerDialog())
            .DisposeWith(_disposables);

        // Initial load
        _relayAdapter.UpdateItems(ViewModel.Relays.ToList());
    }

    private void ShowLogViewerDialog()
    {
        if (Context == null) return;

        var dialogView = LayoutInflater.From(Context)!
            .Inflate(Resource.Layout.dialog_log_viewer, null)!;

        var logContent = dialogView.FindViewById<TextView>(Resource.Id.log_content_text)!;
        var refreshButton = dialogView.FindViewById<MaterialButton>(Resource.Id.log_refresh_button)!;

        var dialog = new MaterialAlertDialogBuilder(Context)
            .SetView(dialogView)!
            .SetPositiveButton("Close", (s, e) =>
            {
                ViewModel.CloseLogViewerCommand.Execute().Subscribe().DisposeWith(_disposables);
            })!
            .Create()!;

        dialog.Show();

        refreshButton.Click += (s, e) =>
        {
            ViewModel.RefreshLogsCommand.Execute().Subscribe().DisposeWith(_disposables);
        };

        ViewModel.WhenAnyValue(x => x.LogContent)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(content =>
            {
                logContent.Text = content ?? "No logs available";
            })
            .DisposeWith(_disposables);

        ViewModel.WhenAnyValue(x => x.ShowLogViewer)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Where(show => !show)
            .Subscribe(_ => { if (dialog.IsShowing) dialog.Dismiss(); })
            .DisposeWith(_disposables);

        // Load initial content
        logContent.Text = ViewModel.LogContent ?? "No logs available";
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
