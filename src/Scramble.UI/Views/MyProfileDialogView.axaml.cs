using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Scramble.UI.Views;

/// <summary>
/// Shared "My Profile" dialog UserControl. Reused by both the desktop window
/// (Scramble.UI/Views/MainWindow.axaml) and the Avalonia mobile shell
/// (Scramble.Mobile.Android/Views/MobileMainView.axaml).
///
/// DataContext is expected to be <see cref="Scramble.Presentation.ViewModels.MainViewModel"/>.
/// The host gates visibility via <c>IsVisible="{Binding ShowMyProfileDialog}"</c>
/// on the surrounding container.
/// </summary>
public partial class MyProfileDialogView : UserControl
{
    public MyProfileDialogView() => AvaloniaXamlLoader.Load(this);
}
