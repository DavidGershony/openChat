using Android.Graphics;
using Android.Views;
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

    private static void BindMediaViews(View itemView, MessageViewModel item)
    {
        var mediaStatus = itemView.FindViewById<TextView>(Resource.Id.media_status)!;
        var loadButton = itemView.FindViewById<MaterialButton>(Resource.Id.load_media_button)!;
        var mediaImage = itemView.FindViewById<ImageView>(Resource.Id.media_image)!;
        var content = itemView.FindViewById<TextView>(Resource.Id.message_content)!;

        if (!item.IsImage)
        {
            // Text message — hide all media views
            mediaStatus.Visibility = ViewStates.Gone;
            loadButton.Visibility = ViewStates.Gone;
            mediaImage.Visibility = ViewStates.Gone;
            content.Visibility = ViewStates.Visible;
            content.Text = item.Content;
            return;
        }

        // Image message — show appropriate state
        content.Visibility = ViewStates.Gone;

        if (item.ShowMediaDisabled)
        {
            mediaStatus.Text = "Media loading disabled\nEnable in Settings > Privacy";
            mediaStatus.Visibility = ViewStates.Visible;
            loadButton.Visibility = ViewStates.Gone;
            mediaImage.Visibility = ViewStates.Gone;
        }
        else if (item.IsMediaLoaded && item.DecryptedMediaBytes != null)
        {
            mediaStatus.Visibility = ViewStates.Gone;
            loadButton.Visibility = ViewStates.Gone;
            mediaImage.Visibility = ViewStates.Visible;
            var bitmap = BitmapFactory.DecodeByteArray(
                item.DecryptedMediaBytes, 0, item.DecryptedMediaBytes.Length);
            mediaImage.SetImageBitmap(bitmap);
        }
        else if (item.IsLoadingMedia)
        {
            mediaStatus.Text = item.MediaSizeDisplay != null
                ? $"Loading... {item.MediaSizeDisplay}"
                : "Loading...";
            mediaStatus.Visibility = ViewStates.Visible;
            loadButton.Visibility = ViewStates.Gone;
            mediaImage.Visibility = ViewStates.Gone;
        }
        else if (!string.IsNullOrEmpty(item.MediaError))
        {
            mediaStatus.Text = item.MediaError;
            mediaStatus.SetTextColor(global::Android.Graphics.Color.ParseColor("#FFEF4444"));
            mediaStatus.Visibility = ViewStates.Visible;
            loadButton.Visibility = ViewStates.Gone;
            mediaImage.Visibility = ViewStates.Gone;
        }
        else if (item.ShowTapToLoad)
        {
            var buttonText = item.ImageDisplayText ?? "Load image";
            if (item.IsUnknownServer)
                buttonText += $"\nUnknown server: {item.ServerHostname}";
            loadButton.Text = buttonText;
            loadButton.Visibility = ViewStates.Visible;
            mediaStatus.Text = "Your IP will be visible to the host";
            mediaStatus.Visibility = ViewStates.Visible;
            mediaImage.Visibility = ViewStates.Gone;

            loadButton.SetOnClickListener(new ActionClickListener(() =>
            {
                item.LoadMediaCommand.Execute().Subscribe();
            }));
        }
        else
        {
            // Fallback: show image display text
            mediaStatus.Text = item.ImageDisplayText ?? "[Encrypted image]";
            mediaStatus.Visibility = ViewStates.Visible;
            loadButton.Visibility = ViewStates.Gone;
            mediaImage.Visibility = ViewStates.Gone;
        }
    }

    private class SentMessageViewHolder : RecyclerView.ViewHolder
    {
        private readonly TextView _content;
        private readonly TextView _timestamp;

        public SentMessageViewHolder(View itemView) : base(itemView)
        {
            _content = itemView.FindViewById<TextView>(Resource.Id.message_content)!;
            _timestamp = itemView.FindViewById<TextView>(Resource.Id.message_timestamp)!;
        }

        public void Bind(MessageViewModel item)
        {
            BindMediaViews(ItemView, item);
            _timestamp.Text = item.Timestamp.ToLocalTime().ToString("HH:mm");
        }
    }

    private class ReceivedMessageViewHolder : RecyclerView.ViewHolder
    {
        private readonly TextView _senderName;
        private readonly TextView _content;
        private readonly TextView _timestamp;

        public ReceivedMessageViewHolder(View itemView) : base(itemView)
        {
            _senderName = itemView.FindViewById<TextView>(Resource.Id.sender_name)!;
            _content = itemView.FindViewById<TextView>(Resource.Id.message_content)!;
            _timestamp = itemView.FindViewById<TextView>(Resource.Id.message_timestamp)!;
        }

        public void Bind(MessageViewModel item)
        {
            _senderName.Text = item.SenderName;
            BindMediaViews(ItemView, item);
            _timestamp.Text = item.Timestamp.ToLocalTime().ToString("HH:mm");
        }
    }

    private class ActionClickListener : Java.Lang.Object, View.IOnClickListener
    {
        private readonly Action _action;
        public ActionClickListener(Action action) => _action = action;
        public void OnClick(View? v) => _action();
    }
}
