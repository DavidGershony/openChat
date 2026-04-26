using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using OpenChat.Presentation.ViewModels;
using ReactiveUI;

namespace OpenChat.UI.Views;

public partial class LoginView : UserControl
{
    public LoginView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void CancelAddAccount_Click(object? sender, RoutedEventArgs e)
    {
        // Navigate up to ShellViewModel to cancel add-account flow
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window?.DataContext is ShellViewModel shell)
        {
            shell.CancelAddAccountCommand.Execute().Subscribe();
        }
    }
}
