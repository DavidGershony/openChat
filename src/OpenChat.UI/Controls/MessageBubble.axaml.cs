using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace OpenChat.UI.Controls;

public partial class MessageBubble : UserControl
{
    public MessageBubble()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
