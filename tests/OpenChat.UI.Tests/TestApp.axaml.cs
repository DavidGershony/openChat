using Avalonia;
using Avalonia.Markup.Xaml;

namespace OpenChat.UI.Tests;

public class TestApp : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
