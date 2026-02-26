using System.Globalization;
using OpenChat.UI.Converters;
using Xunit;

namespace OpenChat.UI.Tests;

public class ConverterTests
{
    [Fact]
    public void DateTimeConverter_Today_ShouldReturnTime()
    {
        // Arrange
        var converter = DateTimeConverter.Instance;
        var today = DateTime.Now;

        // Act
        var result = converter.Convert(today, typeof(string), null, CultureInfo.InvariantCulture);

        // Assert
        Assert.Equal(today.ToString("HH:mm"), result);
    }

    [Fact]
    public void DateTimeConverter_Yesterday_ShouldReturnYesterday()
    {
        // Arrange
        var converter = DateTimeConverter.Instance;
        var yesterday = DateTime.Now.AddDays(-1);

        // Act
        var result = converter.Convert(yesterday, typeof(string), null, CultureInfo.InvariantCulture);

        // Assert
        Assert.Equal("Yesterday", result);
    }

    [Fact]
    public void DateTimeConverter_ThisWeek_ShouldReturnDayName()
    {
        // Arrange
        var converter = DateTimeConverter.Instance;
        var threeDaysAgo = DateTime.Now.AddDays(-3);

        // Act
        var result = converter.Convert(threeDaysAgo, typeof(string), null, CultureInfo.InvariantCulture);

        // Assert
        Assert.Equal(threeDaysAgo.ToString("dddd"), result);
    }

    [Fact]
    public void DateTimeConverter_OlderThanWeek_ShouldReturnDate()
    {
        // Arrange
        var converter = DateTimeConverter.Instance;
        var twoWeeksAgo = DateTime.Now.AddDays(-14);

        // Act
        var result = converter.Convert(twoWeeksAgo, typeof(string), null, CultureInfo.InvariantCulture);

        // Assert
        Assert.Equal(twoWeeksAgo.ToString("MMM d"), result);
    }

    [Fact]
    public void DateTimeConverter_DifferentYear_ShouldIncludeYear()
    {
        // Arrange
        var converter = DateTimeConverter.Instance;
        var lastYear = DateTime.Now.AddYears(-1);

        // Act
        var result = converter.Convert(lastYear, typeof(string), null, CultureInfo.InvariantCulture);

        // Assert
        Assert.Equal(lastYear.ToString("MMM d, yyyy"), result);
    }

    [Fact]
    public void RelativeTimeConverter_JustNow_ShouldReturnJustNow()
    {
        // Arrange
        var converter = RelativeTimeConverter.Instance;
        var now = DateTime.UtcNow;

        // Act
        var result = converter.Convert(now, typeof(string), null, CultureInfo.InvariantCulture);

        // Assert
        Assert.Equal("just now", result);
    }

    [Fact]
    public void RelativeTimeConverter_Minutes_ShouldReturnMinutesAgo()
    {
        // Arrange
        var converter = RelativeTimeConverter.Instance;
        var fiveMinutesAgo = DateTime.UtcNow.AddMinutes(-5);

        // Act
        var result = converter.Convert(fiveMinutesAgo, typeof(string), null, CultureInfo.InvariantCulture);

        // Assert
        Assert.Equal("5m ago", result);
    }

    [Fact]
    public void RelativeTimeConverter_Hours_ShouldReturnHoursAgo()
    {
        // Arrange
        var converter = RelativeTimeConverter.Instance;
        var threeHoursAgo = DateTime.UtcNow.AddHours(-3);

        // Act
        var result = converter.Convert(threeHoursAgo, typeof(string), null, CultureInfo.InvariantCulture);

        // Assert
        Assert.Equal("3h ago", result);
    }

    [Fact]
    public void RelativeTimeConverter_Days_ShouldReturnDaysAgo()
    {
        // Arrange
        var converter = RelativeTimeConverter.Instance;
        var twoDaysAgo = DateTime.UtcNow.AddDays(-2);

        // Act
        var result = converter.Convert(twoDaysAgo, typeof(string), null, CultureInfo.InvariantCulture);

        // Assert
        Assert.Equal("2d ago", result);
    }

    [Fact]
    public void MessageTimestampConverter_ShouldReturnTimeFormat()
    {
        // Arrange
        var converter = MessageTimestampConverter.Instance;
        var dateTime = new DateTime(2024, 1, 15, 14, 30, 0);

        // Act
        var result = converter.Convert(dateTime, typeof(string), null, CultureInfo.InvariantCulture);

        // Assert
        Assert.Equal("14:30", result);
    }

    [Fact]
    public void Converters_NullValue_ShouldReturnEmptyString()
    {
        // Arrange
        var dateTimeConverter = DateTimeConverter.Instance;
        var relativeConverter = RelativeTimeConverter.Instance;
        var timestampConverter = MessageTimestampConverter.Instance;

        // Act & Assert
        Assert.Equal(string.Empty, dateTimeConverter.Convert(null, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal(string.Empty, relativeConverter.Convert(null, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal(string.Empty, timestampConverter.Convert(null, typeof(string), null, CultureInfo.InvariantCulture));
    }
}
