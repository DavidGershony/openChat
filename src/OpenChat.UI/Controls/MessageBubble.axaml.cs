using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using OpenChat.Presentation.ViewModels;

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

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        if (DataContext is MessageViewModel vm)
            vm.IsHovering = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (DataContext is MessageViewModel vm)
            vm.IsHovering = false;
    }
}
