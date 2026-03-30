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

public class NewGroupFragment : Fragment
{
    private readonly MainViewModel _mainViewModel;
    private ChatListViewModel ViewModel => _mainViewModel.ChatListViewModel;
    private CompositeDisposable _disposables = new();

    public NewGroupFragment(MainViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
    }

    public override View? OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
    {
        return inflater.Inflate(Resource.Layout.fragment_new_group, container, false);
    }

    public override void OnViewCreated(View view, Bundle? savedInstanceState)
    {
        base.OnViewCreated(view, savedInstanceState);

        var toolbar = view.FindViewById<MaterialToolbar>(Resource.Id.new_group_toolbar)!;
        var nameInput = view.FindViewById<TextInputEditText>(Resource.Id.new_group_name_input)!;
        var descInput = view.FindViewById<TextInputEditText>(Resource.Id.new_group_desc_input)!;
        var membersInput = view.FindViewById<TextInputEditText>(Resource.Id.new_group_members_input)!;
        var lookupButton = view.FindViewById<MaterialButton>(Resource.Id.lookup_group_keypackages_button)!;
        var statusText = view.FindViewById<TextView>(Resource.Id.group_keypackage_status_text)!;
        var errorText = view.FindViewById<TextView>(Resource.Id.new_group_error)!;
        var relayContainer = view.FindViewById<LinearLayout>(Resource.Id.relay_selection_container)!;
        var createButton = view.FindViewById<MaterialButton>(Resource.Id.create_group_button)!;

        // Back navigation
        toolbar.NavigationClick += (s, e) =>
        {
            ViewModel.CancelNewGroupCommand.Execute().Subscribe().DisposeWith(_disposables);
            ParentFragmentManager.PopBackStack();
        };

        // Create button
        createButton.Click += (s, e) =>
        {
            ViewModel.NewGroupName = nameInput.Text ?? string.Empty;
            ViewModel.NewGroupDescription = descInput.Text ?? string.Empty;
            ViewModel.NewGroupMembers = membersInput.Text ?? string.Empty;
            ViewModel.CreateGroupCommand.Execute().Subscribe().DisposeWith(_disposables);
        };

        // Lookup button
        lookupButton.Click += (s, e) =>
        {
            ViewModel.NewGroupMembers = membersInput.Text ?? string.Empty;
            ViewModel.LookupGroupKeyPackagesCommand.Execute().Subscribe().DisposeWith(_disposables);
        };

        // Observe ViewModel state
        ViewModel.WhenAnyValue(x => x.NewGroupError)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(error =>
            {
                errorText.Text = error ?? "";
                errorText.Visibility = string.IsNullOrEmpty(error) ? ViewStates.Gone : ViewStates.Visible;
            })
            .DisposeWith(_disposables);

        ViewModel.WhenAnyValue(x => x.GroupKeyPackageStatus)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(status =>
            {
                statusText.Text = status ?? "";
                statusText.Visibility = string.IsNullOrEmpty(status) ? ViewStates.Gone : ViewStates.Visible;
            })
            .DisposeWith(_disposables);

        ViewModel.WhenAnyValue(x => x.IsLookingUpGroupKeyPackages)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(loading =>
            {
                lookupButton.Enabled = !loading;
                lookupButton.Text = loading ? "Looking up..." : "Lookup KeyPackages";
            })
            .DisposeWith(_disposables);

        // Set flag before subscribing
        ViewModel.ShowNewGroupDialog = true;

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
        ViewModel.WhenAnyValue(x => x.ShowNewGroupDialog)
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
