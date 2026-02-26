using Microsoft.Extensions.Logging;
using OpenChat.Core.Configuration;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace OpenChat.Core.Logging;

/// <summary>
/// Configures application-wide logging using Serilog.
/// Logs are written to both console and file.
/// </summary>
public static class LoggingConfiguration
{
    private static bool _initialized;
    private static readonly object _lock = new();
    private static ILoggerFactory? _loggerFactory;

    /// <summary>
    /// Gets the path to the log files directory.
    /// </summary>
    public static string LogDirectory { get; private set; } = GetDefaultLogDirectory();

    /// <summary>
    /// Gets the path to the current log file.
    /// </summary>
    public static string CurrentLogFile => Path.Combine(LogDirectory, "openchat-.log");

    /// <summary>
    /// Initializes the logging system. Call this once at application startup.
    /// </summary>
    /// <param name="logDirectory">Optional custom log directory. Defaults to AppData/OpenChat/logs</param>
    /// <param name="minimumLevel">Minimum log level. Defaults to Debug.</param>
    public static void Initialize(string? logDirectory = null, LogEventLevel minimumLevel = LogEventLevel.Debug)
    {
        lock (_lock)
        {
            if (_initialized) return;

            LogDirectory = logDirectory ?? GetDefaultLogDirectory();

            // Ensure log directory exists
            if (!Directory.Exists(LogDirectory))
            {
                Directory.CreateDirectory(LogDirectory);
            }

            var logFilePath = Path.Combine(LogDirectory, "openchat-.log");

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Is(minimumLevel)
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .MinimumLevel.Override("System", LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("Application", "OpenChat")
                .Enrich.WithProperty("Profile", ProfileConfiguration.ProfileName)
                .Enrich.WithProperty("MachineName", Environment.MachineName)
                .WriteTo.Console(
                    outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
                .WriteTo.File(
                    logFilePath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}",
                    shared: true,
                    flushToDiskInterval: TimeSpan.FromSeconds(1))
                .CreateLogger();

            _loggerFactory = new SerilogLoggerFactory(Log.Logger);

            Log.Information("=== OpenChat Application Started ===");
            Log.Information("Log directory: {LogDirectory}", LogDirectory);
            Log.Information("Log level: {Level}", minimumLevel);

            _initialized = true;
        }
    }

    /// <summary>
    /// Creates a logger for the specified type.
    /// </summary>
    public static ILogger<T> CreateLogger<T>()
    {
        EnsureInitialized();
        return _loggerFactory!.CreateLogger<T>();
    }

    /// <summary>
    /// Creates a logger with the specified category name.
    /// </summary>
    public static Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName)
    {
        EnsureInitialized();
        return _loggerFactory!.CreateLogger(categoryName);
    }

    /// <summary>
    /// Gets the ILoggerFactory instance.
    /// </summary>
    public static ILoggerFactory GetLoggerFactory()
    {
        EnsureInitialized();
        return _loggerFactory!;
    }

    /// <summary>
    /// Flushes all pending log entries and shuts down the logging system.
    /// Call this when the application is closing.
    /// </summary>
    public static void Shutdown()
    {
        Log.Information("=== OpenChat Application Shutting Down ===");
        Log.CloseAndFlush();
        _initialized = false;
    }

    /// <summary>
    /// Gets the contents of the most recent log file.
    /// </summary>
    public static string GetRecentLogContents(int maxLines = 500)
    {
        try
        {
            var logFiles = Directory.GetFiles(LogDirectory, "openchat-*.log")
                .OrderByDescending(f => File.GetLastWriteTime(f))
                .ToList();

            if (logFiles.Count == 0)
                return "No log files found.";

            var mostRecentLog = logFiles.First();

            // Open with FileShare.ReadWrite to avoid conflict with Serilog's active writer
            string[] lines;
            using (var stream = new FileStream(mostRecentLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream))
            {
                lines = reader.ReadToEnd().Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            }

            var relevantLines = lines.TakeLast(maxLines).ToArray();

            return $"=== Log file: {Path.GetFileName(mostRecentLog)} ===\n" +
                   $"=== Showing last {relevantLines.Length} of {lines.Length} lines ===\n\n" +
                   string.Join(Environment.NewLine, relevantLines);
        }
        catch (Exception ex)
        {
            return $"Error reading log file: {ex.Message}";
        }
    }

    /// <summary>
    /// Lists all available log files.
    /// </summary>
    public static IEnumerable<string> GetLogFiles()
    {
        if (!Directory.Exists(LogDirectory))
            return Enumerable.Empty<string>();

        return Directory.GetFiles(LogDirectory, "openchat-*.log")
            .OrderByDescending(f => File.GetLastWriteTime(f));
    }

    private static void EnsureInitialized()
    {
        if (!_initialized)
        {
            Initialize();
        }
    }

    private static string GetDefaultLogDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenChat",
            "logs");
    }
}
