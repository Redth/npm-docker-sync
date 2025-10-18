using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace NpmDockerSync.Services;

public class SimpleConsoleFormatter : ConsoleFormatter
{
    private readonly CustomConsoleFormatterOptions _options;

    public SimpleConsoleFormatter(IOptionsMonitor<CustomConsoleFormatterOptions> options)
        : base("simple")
    {
        _options = options.CurrentValue;
    }

    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        var message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);
        if (message == null)
            return;

        // Timestamp
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        textWriter.Write($"{timestamp} ");

        // Log level with color
        var logLevel = logEntry.LogLevel;
        var (levelColor, levelText) = GetLogLevelInfo(logLevel);
        
        if (_options.ColorBehavior == LoggerColorBehavior.Enabled)
        {
            textWriter.Write(levelColor);
            textWriter.Write(levelText.PadRight(5));
            textWriter.Write("\x1b[0m"); // Reset color
        }
        else
        {
            textWriter.Write(levelText.PadRight(5));
        }

        textWriter.Write(" ");

        // Category (simplified)
        var category = logEntry.Category;
        var simpleName = GetSimpleName(category);
        
        if (_options.ColorBehavior == LoggerColorBehavior.Enabled)
        {
            textWriter.Write("\x1b[90m"); // Dark gray
            textWriter.Write($"[{simpleName}]");
            textWriter.Write("\x1b[0m"); // Reset
        }
        else
        {
            textWriter.Write($"[{simpleName}]");
        }

        textWriter.Write(" ");

        // Message
        textWriter.WriteLine(message);

        // Exception
        if (logEntry.Exception != null)
        {
            textWriter.WriteLine(logEntry.Exception.ToString());
        }
    }

    private static (string color, string text) GetLogLevelInfo(LogLevel logLevel)
    {
        return logLevel switch
        {
            LogLevel.Trace => ("\x1b[90m", "trce"),       // Dark gray
            LogLevel.Debug => ("\x1b[36m", "dbug"),       // Cyan
            LogLevel.Information => ("\x1b[32m", "info"), // Green
            LogLevel.Warning => ("\x1b[33m", "warn"),     // Yellow
            LogLevel.Error => ("\x1b[31m", "fail"),       // Red
            LogLevel.Critical => ("\x1b[35m", "crit"),    // Magenta
            _ => ("\x1b[0m", "????")                      // Reset
        };
    }

    private static string GetSimpleName(string category)
    {
        // Remove namespace prefix, just keep the class name
        var lastDot = category.LastIndexOf('.');
        if (lastDot >= 0 && lastDot < category.Length - 1)
        {
            return category.Substring(lastDot + 1);
        }
        return category;
    }
}

public class CustomConsoleFormatterOptions : ConsoleFormatterOptions
{
    public LoggerColorBehavior ColorBehavior { get; set; } = LoggerColorBehavior.Enabled;
}
