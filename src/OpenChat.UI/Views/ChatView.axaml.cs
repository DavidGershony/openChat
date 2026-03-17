using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using OpenChat.Presentation.ViewModels;

namespace OpenChat.UI.Views;

public partial class ChatView : UserControl
{
    private ScrollViewer? _scrollViewer;

    public ChatView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is ChatViewModel vm)
        {
            vm.ScrollToBottomRequested += OnScrollToBottomRequested;
        }
    }

    private void OnScrollToBottomRequested(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _scrollViewer ??= this.FindControl<ScrollViewer>("MessageScrollViewer");
            _scrollViewer?.ScrollToEnd();
        }, DispatcherPriority.Background);
    }
}
