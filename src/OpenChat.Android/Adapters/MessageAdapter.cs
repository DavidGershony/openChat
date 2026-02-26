using Android.Views;
using AndroidX.RecyclerView.Widget;
using OpenChat.Presentation.ViewModels;

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
            _content.Text = item.Content;
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
            _content.Text = item.Content;
            _timestamp.Text = item.Timestamp.ToLocalTime().ToString("HH:mm");
        }
    }
}
