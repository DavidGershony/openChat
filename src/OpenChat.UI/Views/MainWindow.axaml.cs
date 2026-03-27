using System;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using OpenChat.Presentation.ViewModels;
using ReactiveUI;

namespace OpenChat.UI.Views;

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
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(isLoggedIn =>
                    {
                        var mainGrid = this.FindControl<Grid>("MainGrid");
                        var loginView = this.FindControl<UserControl>("LoginViewControl");
                        if (mainGrid != null) mainGrid.IsVisible = isLoggedIn;
                        if (loginView != null) loginView.IsVisible = !isLoggedIn;
                    });

                // Track profile dialog visibility through MainViewModel changes
                var profileOverlay = this.FindControl<Avalonia.Controls.Border>("ProfileDialogOverlay");
                shell.WhenAnyValue(x => x.MainViewModel!.ShowMyProfileDialog)
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(show =>
                    {
                        if (profileOverlay != null) profileOverlay.IsVisible = show;
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
        if (DataContext is ShellViewModel shell && shell.MainViewModel is { } mainVm)
        {
            mainVm.ShowMyProfileCommand.Execute().Subscribe();
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
