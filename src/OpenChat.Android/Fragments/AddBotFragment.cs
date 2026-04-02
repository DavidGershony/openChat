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
        // Back navigation
        toolbar.NavigationClick += (s, e) =>
        {
            ViewModel.CancelAddBotCommand.Execute().Subscribe().DisposeWith(_disposables);
            ParentFragmentManager.PopBackStack();
        };

        // Add button
        addButton.Click += (s, e) =>
        {
            ViewModel.BotNpub = npubInput.Text ?? string.Empty;
            ViewModel.CreateBotChatCommand.Execute().Subscribe().DisposeWith(_disposables);
        };

        // Observe error state
        ViewModel.WhenAnyValue(x => x.AddBotError)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(error =>
            {
                errorText.Text = error ?? "";
                errorText.Visibility = string.IsNullOrEmpty(error) ? ViewStates.Gone : ViewStates.Visible;
            })
            .DisposeWith(_disposables);

        // Show sending overlay while connecting
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

        // Set flag
        ViewModel.ShowAddBotDialog = true;

        // Auto-navigate back on success
        ViewModel.WhenAnyValue(x => x.ShowAddBotDialog)
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
