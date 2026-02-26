using Android.OS;
using Android.Views;
using AndroidX.Fragment.App;
using Google.Android.Material.AppBar;
using Google.Android.Material.Button;
using Google.Android.Material.TextField;
using OpenChat.Presentation.ViewModels;
using ReactiveUI;
using System.Reactive.Disposables;
using System.Reactive.Linq;

namespace OpenChat.Android.Fragments;

public class SettingsFragment : Fragment
{
    private readonly MainViewModel _mainViewModel;
    private SettingsViewModel ViewModel => _mainViewModel.SettingsViewModel;
    private CompositeDisposable _disposables = new();

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
        var aboutInput = view.FindViewById<TextInputEditText>(Resource.Id.about_input)!;
        var saveProfileButton = view.FindViewById<MaterialButton>(Resource.Id.save_profile_button)!;
        var npubText = view.FindViewById<TextView>(Resource.Id.npub_text)!;

        // Toolbar back navigation
        toolbar.NavigationClick += (s, e) =>
        {
            Activity?.OnBackPressedDispatcher.OnBackPressed();
        };

        // Save profile button
        saveProfileButton.Click += (s, e) =>
        {
            ViewModel.DisplayName = displayNameInput.Text;
            ViewModel.About = aboutInput.Text;
            ViewModel.SaveProfileCommand.Execute().Subscribe().DisposeWith(_disposables);
        };

        // Bind ViewModel properties to views
        ViewModel.WhenAnyValue(x => x.DisplayName)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(name =>
            {
                if (displayNameInput.Text != name)
                    displayNameInput.Text = name;
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
    }

    public override void OnDestroyView()
    {
        _disposables.Dispose();
        _disposables = new CompositeDisposable();
        base.OnDestroyView();
    }
}
