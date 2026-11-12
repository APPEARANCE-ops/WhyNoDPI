using System.Windows.Media;

namespace NoDPI.Models;

public enum LogLevel
{
    Info,
    Success,
    Warning,
    Error,
    Engine
}

/// <summary>One row of the in-app journal.</summary>
public sealed class LogEntry
{
    public LogEntry(LogLevel level, string message)
    {
        Level = level;
        Message = message;
        Timestamp = System.DateTime.Now;
        TimeText = Timestamp.ToString("HH:mm:ss");
    }

    public LogLevel Level { get; }
    public string Message { get; }
    public System.DateTime Timestamp { get; }
    public string TimeText { get; }

    /// <summary>Searchable representation used by the journal filter.</summary>
    public string SearchKey => TimeText + " " + Message;

    public Brush AccentBrush => Level switch
    {
        LogLevel.Success => new SolidColorBrush(Color.FromRgb(0x35, 0xD4, 0x99)),
        LogLevel.Warning => new SolidColorBrush(Color.FromRgb(0xF5, 0xB9, 0x42)),
        LogLevel.Error => new SolidColorBrush(Color.FromRgb(0xF1, 0x64, 0x6F)),
        LogLevel.Engine => new SolidColorBrush(Color.FromRgb(0x4D, 0x8D, 0xFF)),
        _ => new SolidColorBrush(Color.FromRgb(0x68, 0x78, 0x91))
    };

    public Brush TextBrush => Level switch
    {
        LogLevel.Error => new SolidColorBrush(Color.FromRgb(0xFF, 0xC7, 0xCD)),
        LogLevel.Warning => new SolidColorBrush(Color.FromRgb(0xFA, 0xE3, 0xB0)),
        LogLevel.Success => new SolidColorBrush(Color.FromRgb(0xB8, 0xF5, 0xDE)),
        LogLevel.Engine => new SolidColorBrush(Color.FromRgb(0xC7, 0xDC, 0xFF)),
        _ => new SolidColorBrush(Color.FromRgb(0xC2, 0xD0, 0xE6))
    };
}
