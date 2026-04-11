using Android.Content;
using Microsoft.Extensions.Logging;
using OpenChat.Core.Logging;

namespace OpenChat.Android.Services;

/// <summary>
/// Manages app theme selection and persistence via SharedPreferences.
/// Themes are defined in Resources/values/styles.xml as separate style entries.
/// </summary>
public static class ThemeService
{
    private const string PrefsName = "openchat_prefs";
    private const string ThemeKey = "selected_theme";
    private static readonly ILogger _logger = LoggingConfiguration.CreateLogger<object>();

    /// <summary>
    /// Available themes with display names and style resource IDs.
    /// Add new themes here and in styles.xml.
    /// </summary>
    public static readonly ThemeInfo[] AvailableThemes = new[]
    {
        new ThemeInfo("nostr_purple", "Nostr Purple", Resource.Style.AppTheme),
        new ThemeInfo("midnight_blue", "Midnight Blue", Resource.Style.AppTheme_MidnightBlue),
        new ThemeInfo("forest_green", "Forest Green", Resource.Style.AppTheme_ForestGreen),
        new ThemeInfo("blood_orange", "Blood Orange", Resource.Style.AppTheme_BloodOrange),
        new ThemeInfo("golden_axe", "Golden Axe", Resource.Style.AppTheme_GoldenAxe),
        new ThemeInfo("monochrome", "Monochrome", Resource.Style.AppTheme_Monochrome),
        new ThemeInfo("cyber_teal", "Cyber Teal", Resource.Style.AppTheme_CyberTeal),
        new ThemeInfo("solarized_dark", "Solarized Dark", Resource.Style.AppTheme_SolarizedDark),
        new ThemeInfo("amoled_black", "AMOLED Black", Resource.Style.AppTheme_AmoledBlack),
    };

    /// <summary>
    /// Gets the saved theme ID, or the default theme if none saved.
    /// </summary>
    public static string GetSavedThemeId(Context context)
    {
        var prefs = context.GetSharedPreferences(PrefsName, FileCreationMode.Private);
        return prefs?.GetString(ThemeKey, "nostr_purple") ?? "nostr_purple";
    }

    /// <summary>
    /// Saves the selected theme ID to SharedPreferences.
    /// </summary>
    public static void SaveThemeId(Context context, string themeId)
    {
        var prefs = context.GetSharedPreferences(PrefsName, FileCreationMode.Private);
        prefs?.Edit()?.PutString(ThemeKey, themeId)?.Apply();
        _logger.LogInformation("Theme saved: {ThemeId}", themeId);
    }

    /// <summary>
    /// Gets the style resource ID for the saved theme.
    /// </summary>
    public static int GetSavedStyleResource(Context context)
    {
        var themeId = GetSavedThemeId(context);
        var theme = AvailableThemes.FirstOrDefault(t => t.Id == themeId);
        return theme?.StyleResource ?? Resource.Style.AppTheme;
    }

    /// <summary>
    /// Gets the ThemeInfo for the saved theme.
    /// </summary>
    public static ThemeInfo GetSavedTheme(Context context)
    {
        var themeId = GetSavedThemeId(context);
        return AvailableThemes.FirstOrDefault(t => t.Id == themeId) ?? AvailableThemes[0];
    }
}

public class ThemeInfo
{
    public string Id { get; }
    public string DisplayName { get; }
    public int StyleResource { get; }

    public ThemeInfo(string id, string displayName, int styleResource)
    {
        Id = id;
        DisplayName = displayName;
        StyleResource = styleResource;
    }
}
