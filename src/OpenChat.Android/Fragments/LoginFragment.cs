using Android.OS;
using Android.Views;
using AndroidX.Fragment.App;
using Google.Android.Material.Button;
using Google.Android.Material.Tabs;
using Google.Android.Material.TextField;
using OpenChat.Presentation.ViewModels;
using ReactiveUI;
using System.Reactive.Disposables;
using System.Reactive.Linq;

namespace OpenChat.Android.Fragments;

public class LoginFragment : Fragment
{
    private readonly MainViewModel _mainViewModel;
    private LoginViewModel ViewModel => _mainViewModel.LoginViewModel;
    private CompositeDisposable _disposables = new();

    public LoginFragment(MainViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
    }

    public override View? OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
    {
        return inflater.Inflate(Resource.Layout.fragment_login, container, false);
    }

    public override void OnViewCreated(View view, Bundle? savedInstanceState)
    {
        base.OnViewCreated(view, savedInstanceState);

        var tabs = view.FindViewById<TabLayout>(Resource.Id.login_tabs)!;
        var errorMessage = view.FindViewById<TextView>(Resource.Id.error_message)!;
        var importKeySection = view.FindViewById<LinearLayout>(Resource.Id.import_key_section)!;
        var generateKeySection = view.FindViewById<LinearLayout>(Resource.Id.generate_key_section)!;
        var privateKeyInput = view.FindViewById<TextInputEditText>(Resource.Id.private_key_input)!;
        var importKeyButton = view.FindViewById<MaterialButton>(Resource.Id.import_key_button)!;
        var generateKeyButton = view.FindViewById<MaterialButton>(Resource.Id.generate_key_button)!;
        var generatedKeysLayout = view.FindViewById<LinearLayout>(Resource.Id.generated_keys_layout)!;
        var generatedNpub = view.FindViewById<TextView>(Resource.Id.generated_npub)!;
        var generatedNsec = view.FindViewById<TextView>(Resource.Id.generated_nsec)!;
        var useGeneratedKeyButton = view.FindViewById<MaterialButton>(Resource.Id.use_generated_key_button)!;

        // Tab selection
        tabs.TabSelected += (s, e) =>
        {
            var method = e.Tab?.Position switch
            {
                0 => LoginMethod.PrivateKey,
                1 => LoginMethod.GenerateNew,
                2 => LoginMethod.ExternalSigner,
                _ => LoginMethod.PrivateKey
            };
            ViewModel.SelectLoginMethodCommand.Execute(method).Subscribe().DisposeWith(_disposables);
        };

        // Button clicks
        importKeyButton.Click += (s, e) =>
        {
            ViewModel.PrivateKeyInput = privateKeyInput.Text ?? string.Empty;
            ViewModel.ImportKeyCommand.Execute().Subscribe().DisposeWith(_disposables);
        };

        generateKeyButton.Click += (s, e) =>
        {
            ViewModel.GenerateNewKeyCommand.Execute().Subscribe().DisposeWith(_disposables);
        };

        useGeneratedKeyButton.Click += (s, e) =>
        {
            ViewModel.UseGeneratedKeyCommand.Execute().Subscribe().DisposeWith(_disposables);
        };

        // Bind ViewModel to views
        ViewModel.WhenAnyValue(x => x.SelectedLoginMethod)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(method =>
            {
                importKeySection.Visibility = method == LoginMethod.PrivateKey ? ViewStates.Visible : ViewStates.Gone;
                generateKeySection.Visibility = method == LoginMethod.GenerateNew ? ViewStates.Visible : ViewStates.Gone;
            })
            .DisposeWith(_disposables);

        ViewModel.WhenAnyValue(x => x.ErrorMessage)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(error =>
            {
                errorMessage.Text = error ?? string.Empty;
                errorMessage.Visibility = string.IsNullOrEmpty(error) ? ViewStates.Gone : ViewStates.Visible;
            })
            .DisposeWith(_disposables);

        ViewModel.WhenAnyValue(x => x.ShowGeneratedKeys)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(show =>
            {
                generatedKeysLayout.Visibility = show ? ViewStates.Visible : ViewStates.Gone;
            })
            .DisposeWith(_disposables);

        ViewModel.WhenAnyValue(x => x.GeneratedNpub)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(npub => generatedNpub.Text = npub ?? string.Empty)
            .DisposeWith(_disposables);

        ViewModel.WhenAnyValue(x => x.GeneratedNsec)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(nsec => generatedNsec.Text = nsec ?? string.Empty)
            .DisposeWith(_disposables);

        ViewModel.WhenAnyValue(x => x.IsLoading)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(loading =>
            {
                importKeyButton.Enabled = !loading;
                generateKeyButton.Enabled = !loading;
                useGeneratedKeyButton.Enabled = !loading;
            })
            .DisposeWith(_disposables);
    }

    public override void OnDestroyView()
    {
        _disposables.Dispose();
        _disposables = new CompositeDisposable();
        base.OnDestroyView();
    }
}
