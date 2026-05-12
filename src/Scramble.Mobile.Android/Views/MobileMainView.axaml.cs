using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Scramble.MobileAndroid.Views;

/// <summary>
/// Top-level mobile shell hosted by App.OnFrameworkInitializationCompleted as the
/// ISingleViewApplicationLifetime.MainView. Reuses LoginView / ChatListView /
/// ChatView / SettingsView from Scramble.UI verbatim so the unified Avalonia UI
/// stays the single source of truth — see CLAUDE.md (Platform targets section).
/// </summary>
public partial class MobileMainView : UserControl
{
    public MobileMainView() => AvaloniaXamlLoader.Load(this);
}
