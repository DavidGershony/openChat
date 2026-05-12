using Avalonia.Controls;

namespace Scramble.UI.Views;

/// <summary>
/// Reusable "New Chat" modal dialog hosted by both the desktop shell
/// (MainWindow) and the mobile shell (MobileMainView).
/// </summary>
public partial class NewChatDialogView : UserControl
{
    public NewChatDialogView()
    {
        InitializeComponent();
    }
}
