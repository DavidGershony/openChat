using Android.Graphics;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using Google.Android.Material.ImageView;
using OpenChat.Presentation.ViewModels;

namespace OpenChat.Android.Adapters;

public class FollowContactAdapter : RecyclerView.Adapter
{
    private List<FollowContactViewModel> _items = new();

    public event EventHandler<FollowContactViewModel>? ItemClick;

    public override int ItemCount => _items.Count;

    public void UpdateItems(IEnumerable<FollowContactViewModel> items)
    {
        _items = items.ToList();
        NotifyDataSetChanged();
    }

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var view = LayoutInflater.From(parent.Context)!
            .Inflate(Resource.Layout.item_contact, parent, false)!;
        return new ContactViewHolder(view);
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
    {
        if (holder is ContactViewHolder vh)
        {
            var item = _items[position];
            vh.Bind(item);
            vh.ItemView.Click -= vh.OnClick;
            vh.OnClick = (s, e) => ItemClick?.Invoke(this, item);
            vh.ItemView.Click += vh.OnClick;
        }
    }

    private class ContactViewHolder : RecyclerView.ViewHolder
    {
        private readonly TextView _avatarText;
        private readonly ShapeableImageView _avatarImage;
        private readonly TextView _name;
        private readonly TextView _npub;
        public EventHandler<View.ClickEventArgs> OnClick = (_, _) => { };

        public ContactViewHolder(View itemView) : base(itemView)
        {
            _avatarText = itemView.FindViewById<TextView>(Resource.Id.contact_avatar_text)!;
            _avatarImage = itemView.FindViewById<ShapeableImageView>(Resource.Id.contact_avatar_image)!;
            _name = itemView.FindViewById<TextView>(Resource.Id.contact_name)!;
            _npub = itemView.FindViewById<TextView>(Resource.Id.contact_npub)!;
        }

        public void Bind(FollowContactViewModel item)
        {
            _name.Text = item.ShownName;
            _npub.Text = item.Npub;

            if (!string.IsNullOrEmpty(item.LocalAvatarPath) && System.IO.File.Exists(item.LocalAvatarPath))
            {
                var bitmap = BitmapFactory.DecodeFile(item.LocalAvatarPath);
                if (bitmap != null)
                {
                    _avatarImage.SetImageBitmap(bitmap);
                    _avatarImage.Visibility = ViewStates.Visible;
                    _avatarText.Visibility = ViewStates.Gone;
                    return;
                }
            }

            _avatarImage.Visibility = ViewStates.Gone;
            _avatarText.Visibility = ViewStates.Visible;
            _avatarText.Text = item.Initial;
        }
    }
}
