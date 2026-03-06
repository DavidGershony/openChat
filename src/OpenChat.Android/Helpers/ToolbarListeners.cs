using Android.Views;
using AndroidX.AppCompat.Widget;

namespace OpenChat.Android;

/// <summary>
/// Simple View.IOnClickListener that invokes an Action.
/// Used for toolbar navigation click since C# events may not fire reliably on MaterialToolbar.
/// </summary>
public class ActionClickListener : Java.Lang.Object, View.IOnClickListener
{
    private readonly Action _action;

    public ActionClickListener(Action action)
    {
        _action = action;
    }

    public void OnClick(View? v)
    {
        _action();
    }
}

/// <summary>
/// Simple Toolbar.IOnMenuItemClickListener that invokes a Func.
/// Used for toolbar menu item clicks since C# events may not fire reliably on MaterialToolbar.
/// </summary>
public class ActionMenuItemClickListener : Java.Lang.Object, AndroidX.AppCompat.Widget.Toolbar.IOnMenuItemClickListener
{
    private readonly Func<IMenuItem?, bool> _handler;

    public ActionMenuItemClickListener(Func<IMenuItem?, bool> handler)
    {
        _handler = handler;
    }

    public bool OnMenuItemClick(IMenuItem? item)
    {
        return _handler(item);
    }
}
