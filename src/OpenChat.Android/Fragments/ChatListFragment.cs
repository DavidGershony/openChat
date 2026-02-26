using Android.OS;
using Android.Views;
using AndroidX.Fragment.App;
using AndroidX.RecyclerView.Widget;
using Google.Android.Material.AppBar;
using Google.Android.Material.FloatingActionButton;
using Google.Android.Material.TextField;
using OpenChat.Android.Adapters;
using OpenChat.Presentation.ViewModels;
using ReactiveUI;
using System.Reactive.Disposables;
using System.Reactive.Linq;

namespace OpenChat.Android.Fragments;

public class ChatListFragment : Fragment
{
    private readonly MainViewModel _mainViewModel;
    private ChatListViewModel ViewModel => _mainViewModel.ChatListViewModel;
    private CompositeDisposable _disposables = new();
    private ChatListAdapter? _adapter;

    public ChatListFragment(MainViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
    }

    public override View? OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
    {
        return inflater.Inflate(Resource.Layout.fragment_chat_list, container, false);
    }

    public override void OnViewCreated(View view, Bundle? savedInstanceState)
    {
        base.OnViewCreated(view, savedInstanceState);

        var toolbar = view.FindViewById<MaterialToolbar>(Resource.Id.toolbar)!;
        var searchInput = view.FindViewById<TextInputEditText>(Resource.Id.search_input)!;
        var recyclerView = view.FindViewById<RecyclerView>(Resource.Id.chat_list_recycler)!;
        var loadingIndicator = view.FindViewById<ProgressBar>(Resource.Id.loading_indicator)!;
        var emptyState = view.FindViewById<LinearLayout>(Resource.Id.empty_state)!;
        var fab = view.FindViewById<FloatingActionButton>(Resource.Id.fab_new_chat)!;

        // Set up toolbar menu
        toolbar.MenuItemClick += (s, e) =>
        {
            if (e.Item?.ItemId == Resource.Id.action_settings)
            {
                (Activity as MainActivity)?.NavigateToSettings();
            }
        };

        // Set up RecyclerView
        _adapter = new ChatListAdapter();
        recyclerView.SetLayoutManager(new LinearLayoutManager(Context));
        recyclerView.SetAdapter(_adapter);

        _adapter.ItemClick += (s, chatItem) =>
        {
            ViewModel.SelectedChat = chatItem;
            (Activity as MainActivity)?.NavigateToChat();
        };

        // FAB - new chat
        fab.Click += (s, e) =>
        {
            ViewModel.ShowNewChatDialog = true;
        };

        // Search text binding
        searchInput.TextChanged += (s, e) =>
        {
            ViewModel.SearchText = searchInput.Text ?? string.Empty;
        };

        // Observe chats collection
        ViewModel.Chats.CollectionChanged += (s, e) =>
        {
            Activity?.RunOnUiThread(() =>
            {
                _adapter.UpdateItems(ViewModel.Chats.ToList());
                emptyState.Visibility = ViewModel.Chats.Count == 0 ? ViewStates.Visible : ViewStates.Gone;
            });
        };

        // Loading state
        ViewModel.WhenAnyValue(x => x.IsLoading)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(loading =>
            {
                loadingIndicator.Visibility = loading ? ViewStates.Visible : ViewStates.Gone;
            })
            .DisposeWith(_disposables);

        // Initial load
        _adapter.UpdateItems(ViewModel.Chats.ToList());
        emptyState.Visibility = ViewModel.Chats.Count == 0 && !ViewModel.IsLoading ? ViewStates.Visible : ViewStates.Gone;
    }

    public override void OnDestroyView()
    {
        _disposables.Dispose();
        _disposables = new CompositeDisposable();
        base.OnDestroyView();
    }
}
