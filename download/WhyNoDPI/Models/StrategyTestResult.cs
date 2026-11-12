using System.Windows.Media;

namespace NoDPI.Models;

public enum StrategyTestState
{
    Passed,
    Partial,
    Failed,
    Running
}

/// <summary>A single row in the automatic strategy test results list.</summary>
public sealed class StrategyTestResult
{
    public StrategyTestResult(string profileName, StrategyTestState state, string detail, int score, int total)
    {
        ProfileName = profileName;
        State = state;
        Detail = detail;
        Score = score;
        Total = total;
    }

    public string ProfileName { get; }
    public StrategyTestState State { get; }
    public string Detail { get; }
    public int Score { get; }
    public int Total { get; }

    public string ScoreText => Total <= 0 ? "—" : $"{Score}/{Total}";

    public string Glyph => State switch
    {
        StrategyTestState.Passed => "✓",
        StrategyTestState.Partial => "◐",
        StrategyTestState.Running => "…",
        _ => "✕"
    };

    public Brush AccentBrush => State switch
    {
        StrategyTestState.Passed => new SolidColorBrush(Color.FromRgb(0x35, 0xD4, 0x99)),
        StrategyTestState.Partial => new SolidColorBrush(Color.FromRgb(0xF5, 0xB9, 0x42)),
        StrategyTestState.Running => new SolidColorBrush(Color.FromRgb(0x4D, 0x8D, 0xFF)),
        _ => new SolidColorBrush(Color.FromRgb(0xF1, 0x64, 0x6F))
    };

    public Brush BadgeBackground => State switch
    {
        StrategyTestState.Passed => new SolidColorBrush(Color.FromRgb(0x10, 0x2E, 0x26)),
        StrategyTestState.Partial => new SolidColorBrush(Color.FromRgb(0x2C, 0x24, 0x13)),
        StrategyTestState.Running => new SolidColorBrush(Color.FromRgb(0x14, 0x24, 0x42)),
        _ => new SolidColorBrush(Color.FromRgb(0x2E, 0x16, 0x20))
    };

    /// <summary>Neutral row fill: the coloured tiles inside carry the status.</summary>
    public Brush RowBackground => new SolidColorBrush(Color.FromArgb(0x0B, 0xFF, 0xFF, 0xFF));

    public string BadgeTag => State switch
    {
        StrategyTestState.Passed => "success",
        StrategyTestState.Partial => "warn",
        StrategyTestState.Running => "accent",
        _ => "danger"
    };
}
