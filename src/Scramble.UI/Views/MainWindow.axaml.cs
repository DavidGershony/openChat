using System;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Scramble.Core.Configuration;
using Scramble.Presentation.ViewModels;
using ReactiveUI;

namespace Scramble.UI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Subscribe to ShellViewModel.IsLoggedIn changes to toggle views programmatically.
        // This bypasses XAML compiled binding issues with cross-thread property changes.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ShellViewModel shell)
            {
                shell.WhenAnyValue(x => x.IsLoggedIn)
                    .ObserveOn(RxSchedulers.MainThreadScheduler)
                    .Subscribe(isLoggedIn =>
                    {
                        var mainGrid = this.FindControl<Grid>("MainGrid");
                        var loginView = this.FindControl<UserControl>("LoginViewControl");
                        if (mainGrid != null) mainGrid.IsVisible = isLoggedIn;
                        if (loginView != null) loginView.IsVisible = !isLoggedIn;

                        // Update window title with active account name
                        Title = "Scramble" + ProfileConfiguration.WindowTitleSuffix;
                    });
            }
        };
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void AvatarButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ShellViewModel shell)
        {
            shell.ToggleAccountSwitcherCommand.Execute().Subscribe();
        }
    }

    private void MinimizeButton_Click(object? sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object? sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
        => Close();
}
