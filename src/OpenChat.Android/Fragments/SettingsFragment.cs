using Android;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.Core.Content;
using AndroidX.RecyclerView.Widget;
using Google.Android.Material.AppBar;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.MaterialSwitch;
using Google.Android.Material.TextField;
using Microsoft.Extensions.Logging;
using OpenChat.Android.Adapters;
using OpenChat.Android.Services;
using OpenChat.Core.Logging;
using OpenChat.Presentation.ViewModels;
using ReactiveUI;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Linq;
using Fragment = AndroidX.Fragment.App.Fragment;

namespace OpenChat.Android.Fragments;

public class SettingsFragment : Fragment
{
    private readonly MainViewModel _mainViewModel;
    private readonly ILogger<SettingsFragment> _logger;
    private SettingsViewModel ViewModel => _mainViewModel.SettingsViewModel;
    private CompositeDisposable _disposables = new();
    private RelayAdapter? _relayAdapter;

    public SettingsFragment(MainViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
        _logger = LoggingConfiguration.CreateLogger<SettingsFragment>();
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
        var reconnectRelaysButton = view.FindViewById<MaterialButton>(Resource.Id.reconnect_relays_button)!;
        var relayRecycler = view.FindViewById<RecyclerView>(Resource.Id.relay_recycler)!;
        var newRelayInput = view.FindViewById<TextInputEditText>(Resource.Id.new_relay_input)!;
        var addRelayButton = view.FindViewById<MaterialButton>(Resource.Id.add_relay_button)!;

        // NIP-65 relay list views
        var publishRelayListButton = view.FindViewById<MaterialButton>(Resource.Id.publish_relay_list_button)!;
        var relayListStatus = view.FindViewById<TextView>(Resource.Id.relay_list_status)!;

        // Key Package views
        var publishButton = view.FindViewById<MaterialButton>(Resource.Id.publish_keypackage_button)!;
        var auditButton = view.FindViewById<MaterialButton>(Resource.Id.audit_keypackage_button)!;
        var keypackageProgress = view.FindViewById<ProgressBar>(Resource.Id.keypackage_progress)!;
        var keypackageStatus = view.FindViewById<TextView>(Resource.Id.keypackage_status)!;

        // Privacy views
        var mip04Toggle = view.FindViewById<MaterialSwitch>(Resource.Id.mip04_toggle)!;
        var mip04Warning = view.FindViewById<TextView>(Resource.Id.mip04_dependency_warning)!;
        var blossomInput = view.FindViewById<TextInputEditText>(Resource.Id.blossom_server_input)!;

        // Developer views
        var viewLogsButton = view.FindViewById<MaterialButton>(Resource.Id.view_logs_button)!;
        var libraryVersionsText = view.FindViewById<TextView>(Resource.Id.library_versions_text)!;
        libraryVersionsText.Text = $"MarmotCs: {ViewModel.MarmotCsVersion}  |  DotnetMls: {ViewModel.DotnetMlsVersion}";

        // Theme views
        var themeButton = view.FindViewById<MaterialButton>(Resource.Id.theme_selector_button)!;

        // Logout button
        var logoutButton = view.FindViewById<MaterialButton>(Resource.Id.logout_button)!;

        // Toolbar back navigation
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

        // Reconnect all relays
        reconnectRelaysButton.Click += (s, e) =>
        {
            reconnectRelaysButton.Enabled = false;
            reconnectRelaysButton.Text = "Reconnecting...";
            _mainViewModel.ReconnectCommand.Execute()
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(
                    _ => { },
                    ex =>
                    {
                        _logger.LogError(ex, "Reconnect all relays failed");
                        reconnectRelaysButton.Enabled = true;
                        reconnectRelaysButton.Text = "Reconnect All";
                        Toast.MakeText(Activity, $"Reconnect failed: {ex.Message}", ToastLength.Long)?.Show();
                    },
                    () =>
                    {
                        reconnectRelaysButton.Enabled = true;
                        reconnectRelaysButton.Text = "Reconnect All";
                        var connected = ViewModel.Relays.Count(r => r.IsConnected);
                        var total = ViewModel.Relays.Count;
                        Toast.MakeText(Activity, $"Connected to {connected}/{total} relays", ToastLength.Short)?.Show();
                    })
                .DisposeWith(_disposables);
        };

        addRelayButton.Click += (s, e) =>
        {
            ViewModel.NewRelayUrl = newRelayInput.Text ?? string.Empty;
            ViewModel.AddRelayCommand.Execute().Subscribe().DisposeWith(_disposables);
            newRelayInput.Text = string.Empty;
        };

        // NIP-65 Publish Relay List
        publishRelayListButton.Click += (s, e) =>
        {
            ViewModel.PublishRelayListCommand.Execute().Subscribe().DisposeWith(_disposables);
        };

        // Key Package
        publishButton.Click += (s, e) =>
        {
            ViewModel.PublishKeyPackageCommand.Execute().Subscribe().DisposeWith(_disposables);
        };

        // Audit Key Packages
        auditButton.Click += (s, e) =>
        {
            ViewModel.AuditKeyPackagesCommand.Execute().Subscribe().DisposeWith(_disposables);
        };

        // MIP-04 Toggle — request media + audio permissions when enabling
        mip04Toggle.Checked = ViewModel.IsMip04Enabled;
        mip04Toggle.CheckedChange += (s, e) =>
        {
            if (!e.IsChecked)
            {
                ViewModel.IsMip04Enabled = false;
                return;
            }

            // Collect all needed permissions
            var needed = new List<string>();
            if (ContextCompat.CheckSelfPermission(RequireContext(), Manifest.Permission.RecordAudio) != Permission.Granted)
                needed.Add(Manifest.Permission.RecordAudio);

            if (OperatingSystem.IsAndroidVersionAtLeast(33))
            {
                if (ContextCompat.CheckSelfPermission(RequireContext(), Manifest.Permission.ReadMediaImages) != Permission.Granted)
                    needed.Add(Manifest.Permission.ReadMediaImages);
                if (ContextCompat.CheckSelfPermission(RequireContext(), Manifest.Permission.ReadMediaVideo) != Permission.Granted)
                    needed.Add(Manifest.Permission.ReadMediaVideo);
            }
            else if (ContextCompat.CheckSelfPermission(RequireContext(), Manifest.Permission.ReadExternalStorage) != Permission.Granted)
            {
                needed.Add(Manifest.Permission.ReadExternalStorage);
            }

            if (needed.Count > 0)
            {
                // Revert toggle until permissions are granted
                mip04Toggle.Checked = false;
                RequestPermissions(needed.ToArray(), 1003);
                return;
            }

            ViewModel.IsMip04Enabled = true;
        };

        // Blossom server URL
        blossomInput.Text = ViewModel.BlossomServerUrl;
        blossomInput.FocusChange += (s, e) =>
        {
            if (!e.HasFocus && !string.IsNullOrWhiteSpace(blossomInput.Text))
                ViewModel.BlossomServerUrl = blossomInput.Text!.Trim();
        };

        // View Logs
        viewLogsButton.Click += (s, e) =>
        {
            ViewModel.ViewLogsCommand.Execute().Subscribe().DisposeWith(_disposables);
        };

        // Export Logs
        var exportLogsButton = view.FindViewById<MaterialButton>(Resource.Id.export_logs_button)!;
        exportLogsButton.Click += (s, e) =>
        {
            ExportLogs();
        };

        // Theme selector
        var currentTheme = ThemeService.GetSavedTheme(RequireContext());
        themeButton.Text = currentTheme.DisplayName;

        themeButton.Click += (s, e) =>
        {
            ShowThemePickerDialog(themeButton);
        };

        // Logout button
        logoutButton.Click += (s, e) =>
        {
            ShowLogoutConfirmation();
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

        // Relay collection — rebind on add/remove
        ViewModel.Relays.CollectionChanged += (s, e) =>
        {
            Activity?.RunOnUiThread(() =>
            {
                _relayAdapter.UpdateItems(ViewModel.Relays.ToList());
            });
            // Subscribe to new items' property changes (for IsConnected updates)
            if (e.NewItems != null)
            {
                foreach (RelayViewModel relay in e.NewItems)
                {
                    relay.PropertyChanged += OnRelayPropertyChanged;
                }
            }
        };

        // Subscribe to existing relays' property changes (for status dot updates)
        foreach (var relay in ViewModel.Relays)
        {
            relay.PropertyChanged += OnRelayPropertyChanged;
        }

        // NIP-65 relay list status
        ViewModel.WhenAnyValue(x => x.PublishRelayListStatus)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(status =>
            {
                relayListStatus.Text = status ?? "";
                relayListStatus.Visibility = string.IsNullOrEmpty(status) ? ViewStates.Gone : ViewStates.Visible;
            })
            .DisposeWith(_disposables);

        // Audit status (reuses keypackage status display)
        ViewModel.WhenAnyValue(x => x.AuditStatus)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(status =>
            {
                if (!string.IsNullOrEmpty(status))
                {
                    keypackageStatus.Text = status;
                    keypackageStatus.Visibility = ViewStates.Visible;
                }
            })
            .DisposeWith(_disposables);

        ViewModel.WhenAnyValue(x => x.IsAuditingKeyPackages)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(auditing =>
            {
                auditButton.Enabled = !auditing;
                auditButton.Text = auditing ? "Auditing..." : "Audit Key Packages";
            })
            .DisposeWith(_disposables);

        // MIP-04 dependency warning
        ViewModel.WhenAnyValue(x => x.Mip04DependencyWarning)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(warning =>
            {
                mip04Warning.Text = warning ?? "";
                mip04Warning.Visibility = string.IsNullOrEmpty(warning) ? ViewStates.Gone : ViewStates.Visible;
            })
            .DisposeWith(_disposables);

        // Blossom server sync
        ViewModel.WhenAnyValue(x => x.BlossomServerUrl)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(url =>
            {
                if (blossomInput.Text != url)
                    blossomInput.Text = url;
            })
            .DisposeWith(_disposables);

        // MIP-04 toggle sync
        ViewModel.WhenAnyValue(x => x.IsMip04Enabled)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(enabled =>
            {
                if (mip04Toggle.Checked != enabled)
                    mip04Toggle.Checked = enabled;
            })
            .DisposeWith(_disposables);

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

    private void ShowThemePickerDialog(MaterialButton themeButton)
    {
        if (Context == null) return;

        var themes = ThemeService.AvailableThemes;
        var names = themes.Select(t => t.DisplayName).ToArray();
        var currentId = ThemeService.GetSavedThemeId(RequireContext());
        var checkedIndex = Array.FindIndex(themes, t => t.Id == currentId);

        new MaterialAlertDialogBuilder(Context)
            .SetTitle("Select Theme")!
            .SetSingleChoiceItems(names, checkedIndex, (s, e) =>
            {
                var selected = themes[e.Which];
                ThemeService.SaveThemeId(RequireContext(), selected.Id);
                themeButton.Text = selected.DisplayName;
                _logger.LogInformation("Theme changed to: {Theme}", selected.DisplayName);

                // Dismiss dialog and recreate activity to apply theme
                (s as global::Android.App.AlertDialog)?.Dismiss();
                Activity?.Recreate();
            })!
            .SetNegativeButton("Cancel", (s, e) => { })!
            .Show();
    }

    private void ShowLogoutConfirmation()
    {
        if (Context == null) return;

        new MaterialAlertDialogBuilder(Context)
            .SetTitle("Log Out")!
            .SetMessage("Are you sure you want to log out?")!
            .SetPositiveButton("Log Out", (s, e) =>
            {
                _mainViewModel.LogoutCommand.Execute().Subscribe().DisposeWith(_disposables);
            })!
            .SetNegativeButton("Cancel", (s, e) => { })!
            .Show();
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

    private void ExportLogs()
    {
        if (Activity == null) return;

        try
        {
            var logFiles = OpenChat.Core.Logging.LoggingConfiguration.GetLogFiles().ToList();
            if (logFiles.Count == 0)
            {
                Toast.MakeText(Activity, "No log files found", ToastLength.Short)?.Show();
                return;
            }

            var latestLogFile = logFiles.First();
            var logFile = new Java.IO.File(latestLogFile);
            var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(
                RequireContext(),
                RequireContext().PackageName + ".fileprovider",
                logFile);

            var intent = new Intent(Intent.ActionSend);
            intent.SetType("text/plain");
            intent.PutExtra(Intent.ExtraStream, uri);
            intent.PutExtra(Intent.ExtraSubject, $"OpenChat Logs - {DateTime.Now:yyyy-MM-dd}");
            intent.AddFlags(ActivityFlags.GrantReadUriPermission);

            _logger.LogInformation("Exporting log file: {File}", latestLogFile);
            StartActivity(Intent.CreateChooser(intent, "Export Logs"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export logs");
            Toast.MakeText(Activity, $"Failed to export: {ex.Message}", ToastLength.Long)?.Show();
        }
    }

    private void OnRelayPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RelayViewModel.IsConnected))
        {
            Activity?.RunOnUiThread(() =>
            {
                _relayAdapter?.UpdateItems(ViewModel.Relays.ToList());
            });
        }
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

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);

        if (requestCode == 1003)
        {
            if (grantResults.Length > 0 && grantResults.All(r => r == Permission.Granted))
            {
                _logger.LogInformation("MIP-04 permissions granted, enabling feature");
                ViewModel.IsMip04Enabled = true;
            }
            else
            {
                _logger.LogWarning("MIP-04 permissions denied");
                Toast.MakeText(Activity, "Media permissions required for MIP-04", ToastLength.Short)?.Show();
            }
        }
    }

    public override void OnDestroyView()
    {
        _disposables.Dispose();
        _disposables = new CompositeDisposable();
        base.OnDestroyView();
    }
}
