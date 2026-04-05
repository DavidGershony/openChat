using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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

    private void OnReplyClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is MessageViewModel msgVm)
        {
            // Walk up the visual tree to find the ChatView's DataContext (ChatViewModel)
            var parent = this.Parent;
            while (parent != null)
            {
                if (parent.DataContext is ChatViewModel chatVm)
                {
                    chatVm.SetReplyTo(msgVm.Message);
                    break;
                }
                parent = (parent as Control)?.Parent;
            }
        }
    }

    private void AudioSlider_PointerPressed(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MessageViewModel vm)
            vm.OnSeekStarted();
    }

    private async void AudioSlider_PointerReleased(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MessageViewModel vm)
            await vm.OnSeekCompleted();
    }
}
