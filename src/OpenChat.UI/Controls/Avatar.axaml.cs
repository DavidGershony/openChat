using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace OpenChat.UI.Controls;

public partial class Avatar : UserControl
{
    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<Avatar, double>(nameof(Size), 48);

    public static readonly StyledProperty<string?> ImageUrlProperty =
        AvaloniaProperty.Register<Avatar, string?>(nameof(ImageUrl));

    public static readonly StyledProperty<string> AvatarNameProperty =
        AvaloniaProperty.Register<Avatar, string>(nameof(AvatarName), "");

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public string? ImageUrl
    {
        get => GetValue(ImageUrlProperty);
        set => SetValue(ImageUrlProperty, value);
    }

    public string AvatarName
    {
        get => GetValue(AvatarNameProperty);
        set => SetValue(AvatarNameProperty, value);
    }

    public string Initial => string.IsNullOrEmpty(AvatarName) ? "?" : AvatarName[0].ToString().ToUpperInvariant();

    public double AvatarFontSize => Size * 0.4;

    public Avatar()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
