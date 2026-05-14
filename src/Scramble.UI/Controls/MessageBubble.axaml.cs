using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Input.Platform;
using Scramble.Presentation.Services;
using Scramble.Presentation.ViewModels;

namespace Scramble.UI.Controls;

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
        // On desktop, show the action bar on hover.
        // On mobile, hover events are unreliable — use tap instead (see OnPointerReleased).
        if (!PlatformContext.IsMobileGlobal && DataContext is MessageViewModel vm)
            vm.IsHovering = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (!PlatformContext.IsMobileGlobal && DataContext is MessageViewModel vm)
            vm.IsHovering = false;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        // On mobile, tap a message to toggle the action bar (reactions/reply/copy).
        // Desktop keeps the existing hover behavior via OnPointerEntered/Exited.
        if (PlatformContext.IsMobileGlobal && DataContext is MessageViewModel vm)
        {
            vm.IsHovering = !vm.IsHovering;
        }
    }

    private void OnReplyClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Avalonia.Controls.Button btn && btn.Tag is MessageViewModel msgVm)
        {
            // Walk up the visual tree to find the ChatView's DataContext (ChatViewModel)
            var parent = this.Parent;
            while (parent != null)
            {
                if (parent.DataContext is ChatViewModel chatVm)
                {
                    chatVm.SetReplyTo(msgVm.Message);
                    // Hide the action bar after action on mobile
                    if (PlatformContext.IsMobileGlobal)
                        msgVm.IsHovering = false;
                    break;
                }
                parent = (parent as Control)?.Parent;
            }
        }
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Avalonia.Controls.Button btn || btn.Tag is not MessageViewModel msgVm || string.IsNullOrWhiteSpace(msgVm.Content))
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard == null)
            return;

        await topLevel.Clipboard.SetTextAsync(msgVm.Content);

        // Hide the action bar after action on mobile
        if (PlatformContext.IsMobileGlobal)
            msgVm.IsHovering = false;
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
