using System.Reactive;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ReactiveUI;

namespace OpenChat.UI.Controls;

public partial class SearchBox : UserControl
{
    public static readonly StyledProperty<string> SearchTextProperty =
        AvaloniaProperty.Register<SearchBox, string>(nameof(SearchText), "");

    public static readonly StyledProperty<string> WatermarkProperty =
        AvaloniaProperty.Register<SearchBox, string>(nameof(Watermark), "Search...");

    public string SearchText
    {
        get => GetValue(SearchTextProperty);
        set => SetValue(SearchTextProperty, value);
    }

    public string Watermark
    {
        get => GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

    public ReactiveCommand<Unit, Unit> ClearCommand { get; }

    public SearchBox()
    {
        ClearCommand = ReactiveCommand.Create(() =>
        {
            SearchText = string.Empty;
        });

        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
