using System.Text.RegularExpressions;

namespace OpenChat.Core.Configuration;

public enum MdkBackend { Rust, Managed }

/// <summary>
/// Holds the active profile name and derived paths for multi-instance support.
/// Call <see cref="SetProfile"/> once at startup before any services are created.
/// </summary>
public static class ProfileConfiguration
{
    public static MdkBackend ActiveMdkBackend { get; private set; } = MdkBackend.Rust;

    public static void SetMdkBackend(MdkBackend backend) => ActiveMdkBackend = backend;

    private static readonly Regex ValidProfileName = new(@"^[a-zA-Z0-9_-]+$", RegexOptions.Compiled);

    /// <summary>
    /// The active profile name (lowercased). "default" when no --profile argument is provided.
    /// </summary>
    public static string ProfileName { get; private set; } = "default";

    /// <summary>
    /// True when a non-default profile is active (i.e. --profile was specified).
    /// </summary>
    public static bool IsCustomProfile { get; private set; }

    /// <summary>
    /// Base data directory for this profile.
    /// Default profile: %LOCALAPPDATA%\OpenChat\
    /// Custom profile: %LOCALAPPDATA%\OpenChat\profiles\{name}\
    /// </summary>
    public static string DataDirectory { get; private set; } = GetDefaultDataDirectory();

    /// <summary>
    /// Full path to the SQLite database file.
    /// </summary>
    public static string DatabasePath => Path.Combine(DataDirectory, "openchat.db");

    /// <summary>
    /// Directory where log files are written.
    /// </summary>
    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>
    /// Suffix for the window title. Empty for the default profile, " - Alice" for profile "alice".
    /// </summary>
    public static string WindowTitleSuffix { get; private set; } = string.Empty;

    /// <summary>
    /// Sets the active profile. Call once at startup before any services are created.
    /// </summary>
    /// <param name="profileName">Profile name from --profile argument, or null for the default profile.</param>
    /// <exception cref="ArgumentException">Thrown when the profile name contains invalid characters.</exception>
    public static void SetProfile(string? profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName))
        {
            // Default profile — backward-compatible paths
            ProfileName = "default";
            IsCustomProfile = false;
            DataDirectory = GetDefaultDataDirectory();
            WindowTitleSuffix = string.Empty;
            return;
        }

        profileName = profileName.Trim();

        if (!ValidProfileName.IsMatch(profileName))
        {
            throw new ArgumentException(
                $"Invalid profile name '{profileName}'. Only letters, digits, hyphens, and underscores are allowed.",
                nameof(profileName));
        }

        var lowerName = profileName.ToLowerInvariant();

        ProfileName = lowerName;
        IsCustomProfile = true;
        DataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenChat",
            "profiles",
            lowerName);
        // Title-case the first letter for display
        WindowTitleSuffix = " - " + char.ToUpperInvariant(profileName[0]) + profileName[1..].ToLowerInvariant();
    }

    private static string GetDefaultDataDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenChat");
    }
}
