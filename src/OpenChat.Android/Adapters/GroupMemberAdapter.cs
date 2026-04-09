using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using OpenChat.Presentation.ViewModels;

namespace OpenChat.Android.Adapters;

public class GroupMemberAdapter : RecyclerView.Adapter
{
    private List<GroupMemberViewModel> _items = new();

    public override int ItemCount => _items.Count;

    public void UpdateItems(List<GroupMemberViewModel> items)
    {
        _items = items;
        NotifyDataSetChanged();
    }

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var view = LayoutInflater.From(parent.Context)!
            .Inflate(Resource.Layout.item_group_member, parent, false)!;
        return new MemberViewHolder(view);
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
    {
        if (holder is MemberViewHolder memberHolder)
        {
            memberHolder.Bind(_items[position]);
        }
    }

    private class MemberViewHolder : RecyclerView.ViewHolder
    {
        private readonly TextView _initial;
        private readonly ImageView _avatar;
        private readonly TextView _name;
        private readonly TextView _youBadge;
        private readonly TextView _npub;

        public MemberViewHolder(View itemView) : base(itemView)
        {
            _initial = itemView.FindViewById<TextView>(Resource.Id.member_initial)!;
            _avatar = itemView.FindViewById<ImageView>(Resource.Id.member_avatar)!;
            _name = itemView.FindViewById<TextView>(Resource.Id.member_name)!;
            _youBadge = itemView.FindViewById<TextView>(Resource.Id.member_you_badge)!;
            _npub = itemView.FindViewById<TextView>(Resource.Id.member_npub)!;
        }

        public void Bind(GroupMemberViewModel member)
        {
            _name.Text = member.DisplayName;
            _initial.Text = member.Initial;
            _initial.Visibility = string.IsNullOrEmpty(member.Picture) ? ViewStates.Visible : ViewStates.Gone;
            _avatar.Visibility = string.IsNullOrEmpty(member.Picture) ? ViewStates.Gone : ViewStates.Visible;
            _youBadge.Visibility = member.IsCurrentUser ? ViewStates.Visible : ViewStates.Gone;
            _npub.Text = member.Npub ?? $"{member.PublicKeyHex[..12]}...";
        }
    }
}
