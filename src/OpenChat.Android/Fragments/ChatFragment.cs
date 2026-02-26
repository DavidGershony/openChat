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

public class ChatFragment : Fragment
{
    private readonly MainViewModel _mainViewModel;
    private ChatViewModel ViewModel => _mainViewModel.ChatViewModel;
    private CompositeDisposable _disposables = new();
    private MessageAdapter? _adapter;
    private LinearLayoutManager? _layoutManager;

    public ChatFragment(MainViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
    }

    public override View? OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
    {
        return inflater.Inflate(Resource.Layout.fragment_chat, container, false);
    }

    public override void OnViewCreated(View view, Bundle? savedInstanceState)
    {
        base.OnViewCreated(view, savedInstanceState);

        var toolbar = view.FindViewById<MaterialToolbar>(Resource.Id.chat_toolbar)!;
        var recyclerView = view.FindViewById<RecyclerView>(Resource.Id.messages_recycler)!;
        var messageInput = view.FindViewById<TextInputEditText>(Resource.Id.message_input)!;
        var sendButton = view.FindViewById<FloatingActionButton>(Resource.Id.send_button)!;

        // Toolbar setup
        toolbar.NavigationClick += (s, e) =>
        {
            Activity?.OnBackPressedDispatcher.OnBackPressed();
        };

        // RecyclerView setup
        _layoutManager = new LinearLayoutManager(Context);
        _layoutManager.StackFromEnd = true;
        recyclerView.SetLayoutManager(_layoutManager);

        _adapter = new MessageAdapter();
        recyclerView.SetAdapter(_adapter);

        // Send button
        sendButton.Click += (s, e) =>
        {
            ViewModel.MessageText = messageInput.Text ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(ViewModel.MessageText))
            {
                ViewModel.SendMessageCommand.Execute().Subscribe().DisposeWith(_disposables);
                messageInput.Text = string.Empty;
            }
        };

        // Bind chat name to toolbar
        ViewModel.WhenAnyValue(x => x.ChatName)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(name => toolbar.Title = name)
            .DisposeWith(_disposables);

        // Observe messages collection
        ViewModel.Messages.CollectionChanged += (s, e) =>
        {
            Activity?.RunOnUiThread(() =>
            {
                _adapter.UpdateItems(ViewModel.Messages.ToList());
                if (ViewModel.Messages.Count > 0)
                {
                    recyclerView.ScrollToPosition(ViewModel.Messages.Count - 1);
                }
            });
        };

        // Bind sending state
        ViewModel.WhenAnyValue(x => x.IsSending)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(sending =>
            {
                sendButton.Enabled = !sending;
            })
            .DisposeWith(_disposables);

        // Clear message input when MessageText is cleared (after send)
        ViewModel.WhenAnyValue(x => x.MessageText)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(text =>
            {
                if (string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(messageInput.Text))
                {
                    messageInput.Text = string.Empty;
                }
            })
            .DisposeWith(_disposables);

        // Initial load
        _adapter.UpdateItems(ViewModel.Messages.ToList());
    }

    public override void OnDestroyView()
    {
        _disposables.Dispose();
        _disposables = new CompositeDisposable();
        base.OnDestroyView();
    }
}
