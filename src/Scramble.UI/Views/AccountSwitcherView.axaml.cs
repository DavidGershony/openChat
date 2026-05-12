using System;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Scramble.Presentation.ViewModels;

namespace Scramble.UI.Views;

/// <summary>
/// Shared account-switcher dialog UserControl. Reused by both the desktop window
/// (Scramble.UI/Views/MainWindow.axaml) and the Avalonia mobile shell
/// (Scramble.Mobile.Android/Views/MobileMainView.axaml).
///
/// DataContext is expected to be <see cref="ShellViewModel"/>. The host gates
/// visibility via <c>IsVisible="{Binding ShowAccountSwitcher}"</c> on the
/// surrounding container.
///
/// Account-item taps and the "View Profile" button route through code-behind
/// because they need access to <see cref="ShellViewModel"/> methods that take
/// a parameter (SwitchAccountAsync) or chain to <see cref="MainViewModel"/>
/// state (ShowMyProfileCommand) which is more concise here than in compiled
/// XAML bindings.
/// </summary>
public partial class AccountSwitcherView : UserControl
{
    public AccountSwitcherView() => AvaloniaXamlLoader.Load(this);

    private void AccountItem_Click(object? sender, RoutedEventArgs e)
    {
        // Fully qualify Avalonia.Controls.Button — Scramble.UI multi-targets
        // net10.0-android (via Scramble.Mobile.Android consumer) where the
        // Android.Widget.Button namespace is also in scope and ambiguates.
        if (sender is Avalonia.Controls.Button btn && btn.Tag is string pubKeyHex && DataContext is ShellViewModel shell)
        {
            _ = shell.SwitchAccountAsync(pubKeyHex);
        }
    }

    private void ViewProfile_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ShellViewModel shell)
        {
            shell.ShowAccountSwitcher = false;
            if (shell.MainViewModel is { } mainVm)
            {
                mainVm.ShowMyProfileCommand.Execute().Subscribe();
            }
        }
    }
}
