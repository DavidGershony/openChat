using System.Globalization;
using Avalonia.Data.Converters;

namespace OpenChat.UI.Converters;

public class DateTimeConverter : IValueConverter
{
    public static readonly DateTimeConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DateTime dateTime)
            return string.Empty;

        var now = DateTime.Now;
        var today = now.Date;
        var yesterday = today.AddDays(-1);
        var thisWeek = today.AddDays(-7);

        if (dateTime.Date == today)
        {
            return dateTime.ToString("HH:mm");
        }

        if (dateTime.Date == yesterday)
        {
            return "Yesterday";
        }

        if (dateTime.Date > thisWeek)
        {
            return dateTime.ToString("dddd");
        }

        if (dateTime.Year == now.Year)
        {
            return dateTime.ToString("MMM d");
        }

        return dateTime.ToString("MMM d, yyyy");
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class RelativeTimeConverter : IValueConverter
{
    public static readonly RelativeTimeConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DateTime dateTime)
            return string.Empty;

        var now = DateTime.UtcNow;
        var diff = now - dateTime;

        if (diff.TotalSeconds < 60)
            return "just now";

        if (diff.TotalMinutes < 60)
            return $"{(int)diff.TotalMinutes}m ago";

        if (diff.TotalHours < 24)
            return $"{(int)diff.TotalHours}h ago";

        if (diff.TotalDays < 7)
            return $"{(int)diff.TotalDays}d ago";

        if (diff.TotalDays < 30)
            return $"{(int)(diff.TotalDays / 7)}w ago";

        if (diff.TotalDays < 365)
            return $"{(int)(diff.TotalDays / 30)}mo ago";

        return $"{(int)(diff.TotalDays / 365)}y ago";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class MessageTimestampConverter : IValueConverter
{
    public static readonly MessageTimestampConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DateTime dateTime)
            return string.Empty;

        return dateTime.ToString("HH:mm");
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
