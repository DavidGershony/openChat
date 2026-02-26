using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace OpenChat.UI.Views;

public partial class ChatListView : UserControl
{
    public ChatListView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
