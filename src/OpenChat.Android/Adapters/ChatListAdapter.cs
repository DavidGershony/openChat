using Android.Views;
using AndroidX.RecyclerView.Widget;
using OpenChat.Presentation.ViewModels;

namespace OpenChat.Android.Adapters;

public class ChatListAdapter : RecyclerView.Adapter
{
    private List<ChatItemViewModel> _items = new();

    public event EventHandler<ChatItemViewModel>? ItemClick;

    public override int ItemCount => _items.Count;

    public void UpdateItems(List<ChatItemViewModel> items)
    {
        _items = items;
        NotifyDataSetChanged();
    }

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var view = LayoutInflater.From(parent.Context)!
            .Inflate(Resource.Layout.item_chat, parent, false)!;
        return new ChatViewHolder(view);
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
    {
        if (holder is ChatViewHolder chatHolder)
        {
            var item = _items[position];
            chatHolder.Bind(item);
            chatHolder.ItemView.Click += (s, e) => ItemClick?.Invoke(this, item);
        }
    }

    private class ChatViewHolder : RecyclerView.ViewHolder
    {
        private readonly TextView _avatar;
        private readonly TextView _name;
        private readonly TextView _lastMessage;
        private readonly TextView _timestamp;

        public ChatViewHolder(View itemView) : base(itemView)
        {
            _avatar = itemView.FindViewById<TextView>(Resource.Id.chat_avatar)!;
            _name = itemView.FindViewById<TextView>(Resource.Id.chat_name)!;
            _lastMessage = itemView.FindViewById<TextView>(Resource.Id.chat_last_message)!;
            _timestamp = itemView.FindViewById<TextView>(Resource.Id.chat_timestamp)!;
        }

        public void Bind(ChatItemViewModel item)
        {
            _name.Text = item.Name;
            _lastMessage.Text = item.LastMessage ?? "";
            _timestamp.Text = item.LastActivityText ?? "";

            // Avatar: first letter of name
            var initial = string.IsNullOrEmpty(item.Name) ? "?" : item.Name[..1].ToUpper();
            _avatar.Text = initial;
        }
    }
}
