using Avalonia;
using Avalonia.Markup.Xaml;

namespace Scramble.UI.Tests;

public class TestApp : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
