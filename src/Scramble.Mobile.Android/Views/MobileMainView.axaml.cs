using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Scramble.Presentation.ViewModels;

namespace Scramble.MobileAndroid.Views;

/// <summary>
/// Top-level mobile shell hosted by App.OnFrameworkInitializationCompleted as the
/// ISingleViewApplicationLifetime.MainView. Reuses LoginView / ChatListView /
/// ChatView / SettingsView / AccountSwitcherView / MyProfileDialogView from
/// Scramble.UI verbatim so the unified Avalonia UI stays the single source of
/// truth — see CLAUDE.md (Platform targets section).
/// </summary>
public partial class MobileMainView : UserControl
{
    public MobileMainView() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Top-bar avatar tap → toggle the account switcher. Mirrors
    /// MainWindow.axaml.cs AvatarButton_Click on desktop.
    /// </summary>
    private void AvatarButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ShellViewModel shell)
        {
            shell.ToggleAccountSwitcherCommand.Execute().Subscribe();
        }
    }
}
