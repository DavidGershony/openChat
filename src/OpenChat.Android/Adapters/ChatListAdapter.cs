using Android.Views;
using AndroidX.RecyclerView.Widget;
using OpenChat.Presentation.ViewModels;

namespace OpenChat.Android.Adapters;

public class ChatListAdapter : RecyclerView.Adapter
{
    private List<ChatItemViewModel> _items = new();

    public event EventHandler<ChatItemViewModel>? ItemClick;
    public event EventHandler<ChatItemViewModel>? ItemLongClick;

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
            chatHolder.ItemView.LongClick += (s, e) => ItemLongClick?.Invoke(this, item);
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
            _lastMessage.Text = item.LastMessagePreview ?? "";
            _timestamp.Text = FormatRelativeTime(item.LastActivityAt);

            // Avatar: first letter of name
            var initial = string.IsNullOrEmpty(item.Name) ? "?" : item.Name[..1].ToUpper();
            _avatar.Text = initial;
        }

        private static string FormatRelativeTime(DateTime dateTime)
        {
            if (dateTime == default) return "";
            var elapsed = DateTime.Now - dateTime;
            if (elapsed.TotalMinutes < 1) return "now";
            if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes}m";
            if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours}h";
            if (elapsed.TotalDays < 7) return $"{(int)elapsed.TotalDays}d";
            return dateTime.ToString("MMM d");
        }
    }
}
