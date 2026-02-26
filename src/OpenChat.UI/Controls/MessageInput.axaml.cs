using System.Reactive;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using ReactiveUI;

namespace OpenChat.UI.Controls;

public partial class MessageInput : UserControl
{
    public static readonly StyledProperty<string> MessageTextProperty =
        AvaloniaProperty.Register<MessageInput, string>(nameof(MessageText), "");

    public static readonly StyledProperty<ICommand?> SendCommandProperty =
        AvaloniaProperty.Register<MessageInput, ICommand?>(nameof(SendCommand));

    public static readonly StyledProperty<ICommand?> AttachCommandProperty =
        AvaloniaProperty.Register<MessageInput, ICommand?>(nameof(AttachCommand));

    public static readonly StyledProperty<bool> IsSendingProperty =
        AvaloniaProperty.Register<MessageInput, bool>(nameof(IsSending));

    public string MessageText
    {
        get => GetValue(MessageTextProperty);
        set => SetValue(MessageTextProperty, value);
    }

    public ICommand? SendCommand
    {
        get => GetValue(SendCommandProperty);
        set => SetValue(SendCommandProperty, value);
    }

    public ICommand? AttachCommand
    {
        get => GetValue(AttachCommandProperty);
        set => SetValue(AttachCommandProperty, value);
    }

    public bool IsSending
    {
        get => GetValue(IsSendingProperty);
        set => SetValue(IsSendingProperty, value);
    }

    public bool CanSend => !string.IsNullOrWhiteSpace(MessageText) && !IsSending;

    public MessageInput()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            if (SendCommand?.CanExecute(null) == true)
            {
                SendCommand.Execute(null);
            }
            e.Handled = true;
        }
    }
}
