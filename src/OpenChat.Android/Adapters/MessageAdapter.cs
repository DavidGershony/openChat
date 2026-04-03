using Android.Graphics;
using Android.Views;
using Android.Widget;
using AndroidX.Core.Content;
using AndroidX.RecyclerView.Widget;
using Google.Android.Material.Button;
using OpenChat.Presentation.ViewModels;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using ReactiveUI;

namespace OpenChat.Android.Adapters;

public class MessageAdapter : RecyclerView.Adapter
{
    private const int ViewTypeSent = 0;
    private const int ViewTypeReceived = 1;

    private List<MessageViewModel> _items = new();

    public override int ItemCount => _items.Count;

    public void UpdateItems(List<MessageViewModel> items)
    {
        _items = items;
        NotifyDataSetChanged();
    }

    public override int GetItemViewType(int position)
    {
        return _items[position].IsFromCurrentUser ? ViewTypeSent : ViewTypeReceived;
    }

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var layoutId = viewType == ViewTypeSent
            ? Resource.Layout.item_message_sent
            : Resource.Layout.item_message_received;

        var view = LayoutInflater.From(parent.Context)!
            .Inflate(layoutId, parent, false)!;

        return viewType == ViewTypeSent
            ? new SentMessageViewHolder(view)
            : new ReceivedMessageViewHolder(view);
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
    {
        var item = _items[position];

        if (holder is SentMessageViewHolder sent)
        {
            sent.Bind(item);
        }
        else if (holder is ReceivedMessageViewHolder received)
        {
            received.Bind(item);
        }
    }

    private static void BindMediaViews(View itemView, MessageViewModel item, CompositeDisposable disposables)
    {
        var mediaStatus = itemView.FindViewById<TextView>(Resource.Id.media_status)!;
        var loadButton = itemView.FindViewById<MaterialButton>(Resource.Id.load_media_button)!;
        var mediaImage = itemView.FindViewById<ImageView>(Resource.Id.media_image)!;
        var content = itemView.FindViewById<TextView>(Resource.Id.message_content)!;
        var audioPlayer = itemView.FindViewById<LinearLayout>(Resource.Id.audio_player)!;
        var audioPlayButton = itemView.FindViewById<ImageButton>(Resource.Id.audio_play_button)!;
        var audioProgress = itemView.FindViewById<SeekBar>(Resource.Id.audio_progress)!;
        var audioDuration = itemView.FindViewById<TextView>(Resource.Id.audio_duration)!;

        if (item.IsTextMessage)
        {
            // Text message — hide all media views
            mediaStatus.Visibility = ViewStates.Gone;
            loadButton.Visibility = ViewStates.Gone;
            mediaImage.Visibility = ViewStates.Gone;
            audioPlayer.Visibility = ViewStates.Gone;
            content.Visibility = ViewStates.Visible;
            content.Text = item.Content;
            return;
        }

        // Media message (image or audio) — hide text content
        content.Visibility = ViewStates.Gone;

        if (item.ShowMediaDisabled)
        {
            mediaStatus.Text = "Media loading disabled\nEnable in Settings > Privacy";
            mediaStatus.Visibility = ViewStates.Visible;
            loadButton.Visibility = ViewStates.Gone;
            mediaImage.Visibility = ViewStates.Gone;
            audioPlayer.Visibility = ViewStates.Gone;
        }
        else if (item.IsAudio && item.IsMediaLoaded)
        {
            // Audio loaded — show player
            mediaStatus.Visibility = ViewStates.Gone;
            loadButton.Visibility = ViewStates.Gone;
            mediaImage.Visibility = ViewStates.Gone;
            audioPlayer.Visibility = ViewStates.Visible;

            audioDuration.Text = item.AudioDurationText ?? "0:00";

            audioPlayButton.SetOnClickListener(new ActionClickListener(() =>
            {
                item.ToggleAudioCommand.Execute().Subscribe();
            }));

            // Observe playing state
            item.WhenAnyValue(x => x.IsPlayingAudio)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(playing =>
                {
                    audioPlayButton.SetImageResource(playing
                        ? global::Android.Resource.Drawable.IcMediaPause
                        : global::Android.Resource.Drawable.IcMediaPlay);
                })
                .DisposeWith(disposables);

            // Observe progress
            item.WhenAnyValue(x => x.AudioProgress)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(progress =>
                {
                    audioProgress.Progress = (int)(progress * 100);
                })
                .DisposeWith(disposables);

            // Observe duration text updates
            item.WhenAnyValue(x => x.AudioDurationText)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(dur =>
                {
                    audioDuration.Text = dur ?? "0:00";
                })
                .DisposeWith(disposables);
        }
        else if (item.IsImage && item.IsMediaLoaded && item.DecryptedMediaBytes != null)
        {
            // Image loaded — show bitmap
            mediaStatus.Visibility = ViewStates.Gone;
            loadButton.Visibility = ViewStates.Gone;
            mediaImage.Visibility = ViewStates.Visible;
            audioPlayer.Visibility = ViewStates.Gone;
            var bitmap = BitmapFactory.DecodeByteArray(
                item.DecryptedMediaBytes, 0, item.DecryptedMediaBytes.Length);
            mediaImage.SetImageBitmap(bitmap);
        }
        else if (item.IsFile && item.IsMediaLoaded)
        {
            // File downloaded
            mediaStatus.Text = $"{item.ImageDisplayText}\nDownloaded";
            mediaStatus.SetTextColor(new global::Android.Graphics.Color(ContextCompat.GetColor(itemView.Context!, Resource.Color.status_success)));
            mediaStatus.Visibility = ViewStates.Visible;
            loadButton.Visibility = ViewStates.Gone;
            mediaImage.Visibility = ViewStates.Gone;
            audioPlayer.Visibility = ViewStates.Gone;
        }
        else if (item.IsLoadingMedia)
        {
            mediaStatus.Text = item.MediaSizeDisplay != null
                ? $"Loading... {item.MediaSizeDisplay}"
                : "Loading...";
            mediaStatus.Visibility = ViewStates.Visible;
            loadButton.Visibility = ViewStates.Gone;
            mediaImage.Visibility = ViewStates.Gone;
            audioPlayer.Visibility = ViewStates.Gone;
        }
        else if (!string.IsNullOrEmpty(item.MediaError))
        {
            mediaStatus.Text = item.MediaError;
            mediaStatus.SetTextColor(new global::Android.Graphics.Color(ContextCompat.GetColor(itemView.Context!, Resource.Color.status_error)));
            mediaStatus.Visibility = ViewStates.Visible;
            loadButton.Visibility = ViewStates.Gone;
            mediaImage.Visibility = ViewStates.Gone;
            audioPlayer.Visibility = ViewStates.Gone;
        }
        else if (item.ShowTapToLoad)
        {
            var buttonText = item.IsAudio
                ? (item.ImageDisplayText ?? "Load voice message")
                : item.IsFile
                    ? $"Download {item.ImageDisplayText ?? "file"}"
                    : (item.ImageDisplayText ?? "Load image");
            if (item.IsUnknownServer)
                buttonText += $"\nUnknown server: {item.ServerHostname}";
            loadButton.Text = buttonText;
            loadButton.Visibility = ViewStates.Visible;
            mediaStatus.Text = "Your IP will be visible to the host";
            mediaStatus.Visibility = ViewStates.Visible;
            mediaImage.Visibility = ViewStates.Gone;
            audioPlayer.Visibility = ViewStates.Gone;

            loadButton.SetOnClickListener(new ActionClickListener(() =>
            {
                item.LoadMediaCommand.Execute().Subscribe();
            }));
        }
        else
        {
            // Fallback
            mediaStatus.Text = item.ImageDisplayText ?? "[Encrypted media]";
            mediaStatus.Visibility = ViewStates.Visible;
            loadButton.Visibility = ViewStates.Gone;
            mediaImage.Visibility = ViewStates.Gone;
            audioPlayer.Visibility = ViewStates.Gone;
        }
    }

    private static readonly string[] ReactionEmojis = { "\ud83d\udc4d", "\u2764\ufe0f", "\ud83d\ude02", "\ud83d\ude2e", "\ud83d\ude22", "\ud83d\udd25" };

    private static void BindReactions(View itemView, MessageViewModel item, CompositeDisposable disposables)
    {
        var reactionsDisplay = itemView.FindViewById<TextView>(Resource.Id.reactions_display);
        if (reactionsDisplay == null) return;

        item.WhenAnyValue(x => x.ReactionsDisplay)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(display =>
            {
                if (string.IsNullOrEmpty(display))
                {
                    reactionsDisplay.Visibility = ViewStates.Gone;
                }
                else
                {
                    reactionsDisplay.Text = display;
                    reactionsDisplay.Visibility = ViewStates.Visible;
                }
            })
            .DisposeWith(disposables);

        // Long-press to show reaction picker
        itemView.SetOnLongClickListener(new ActionLongClickListener(() =>
        {
            ShowReactionPicker(itemView, item);
        }));
    }

    private static void ShowReactionPicker(View anchor, MessageViewModel item)
    {
        var context = anchor.Context;
        if (context == null) return;

        var popup = new AndroidX.AppCompat.Widget.PopupMenu(context, anchor);
        for (var i = 0; i < ReactionEmojis.Length; i++)
        {
            popup.Menu?.Add(0, i, i, ReactionEmojis[i]);
        }
        popup.MenuItemClick += (s, e) =>
        {
            var emoji = ReactionEmojis[e.Item!.ItemId];
            item.ReactCommand?.Execute(emoji).Subscribe();
        };
        popup.Show();
    }

    private class SentMessageViewHolder : RecyclerView.ViewHolder
    {
        private readonly TextView _content;
        private readonly TextView _timestamp;
        private CompositeDisposable _disposables = new();

        public SentMessageViewHolder(View itemView) : base(itemView)
        {
            _content = itemView.FindViewById<TextView>(Resource.Id.message_content)!;
            _timestamp = itemView.FindViewById<TextView>(Resource.Id.message_timestamp)!;
        }

        public void Bind(MessageViewModel item)
        {
            _disposables.Dispose();
            _disposables = new CompositeDisposable();

            BindMediaViews(ItemView, item, _disposables);
            BindReactions(ItemView, item, _disposables);
            _timestamp.Text = item.Timestamp.ToLocalTime().ToString("HH:mm");
        }
    }

    private class ReceivedMessageViewHolder : RecyclerView.ViewHolder
    {
        private readonly TextView _senderName;
        private readonly TextView _content;
        private readonly TextView _timestamp;
        private CompositeDisposable _disposables = new();

        public ReceivedMessageViewHolder(View itemView) : base(itemView)
        {
            _senderName = itemView.FindViewById<TextView>(Resource.Id.sender_name)!;
            _content = itemView.FindViewById<TextView>(Resource.Id.message_content)!;
            _timestamp = itemView.FindViewById<TextView>(Resource.Id.message_timestamp)!;
        }

        public void Bind(MessageViewModel item)
        {
            _disposables.Dispose();
            _disposables = new CompositeDisposable();

            _senderName.Text = item.SenderName;
            BindMediaViews(ItemView, item, _disposables);
            BindReactions(ItemView, item, _disposables);
            _timestamp.Text = item.Timestamp.ToLocalTime().ToString("HH:mm");
        }
    }

    private class ActionLongClickListener : Java.Lang.Object, View.IOnLongClickListener
    {
        private readonly Action _action;
        public ActionLongClickListener(Action action) => _action = action;
        public bool OnLongClick(View? v) { _action(); return true; }
    }

    private class ActionClickListener : Java.Lang.Object, View.IOnClickListener
    {
        private readonly Action _action;
        public ActionClickListener(Action action) => _action = action;
        public void OnClick(View? v) => _action();
    }
}
