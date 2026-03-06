using Android.Views;
using AndroidX.RecyclerView.Widget;
using OpenChat.Presentation.ViewModels;

namespace OpenChat.Android.Adapters;

public class RelayAdapter : RecyclerView.Adapter
{
    private List<RelayViewModel> _items = new();

    public event EventHandler<RelayViewModel>? RemoveClick;

    public override int ItemCount => _items.Count;

    public void UpdateItems(List<RelayViewModel> items)
    {
        _items = items;
        NotifyDataSetChanged();
    }

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var view = LayoutInflater.From(parent.Context)!
            .Inflate(Resource.Layout.item_relay, parent, false)!;
        return new RelayViewHolder(view);
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
    {
        if (holder is RelayViewHolder relayHolder)
        {
            var item = _items[position];
            relayHolder.Bind(item);
            relayHolder.RemoveButton.Click += (s, e) => RemoveClick?.Invoke(this, item);
        }
    }

    private class RelayViewHolder : RecyclerView.ViewHolder
    {
        private readonly View _statusDot;
        private readonly TextView _url;
        public ImageButton RemoveButton { get; }

        public RelayViewHolder(View itemView) : base(itemView)
        {
            _statusDot = itemView.FindViewById<View>(Resource.Id.relay_status_dot)!;
            _url = itemView.FindViewById<TextView>(Resource.Id.relay_url)!;
            RemoveButton = itemView.FindViewById<ImageButton>(Resource.Id.relay_remove_button)!;
        }

        public void Bind(RelayViewModel item)
        {
            _url.Text = item.Url;
            _statusDot.SetBackgroundColor(item.IsConnected
                ? global::Android.Graphics.Color.ParseColor("#FF10B981")   // success green
                : global::Android.Graphics.Color.ParseColor("#FFEF4444")); // error red
        }
    }
}
