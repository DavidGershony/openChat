using System;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace OpenChat.UI.Converters;

/// <summary>
/// Converts byte[] PNG data to an Avalonia Bitmap for Image binding.
/// Used for QR codes that are generated as platform-neutral PNG bytes.
/// </summary>
public class PngBytesToBitmapConverter : IValueConverter
{
    public static readonly PngBytesToBitmapConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is byte[] pngBytes && pngBytes.Length > 0)
        {
            using var ms = new MemoryStream(pngBytes);
            return new Bitmap(ms);
        }
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
