using System;
using System.IO;
using System.Text.Json;

namespace NoDPI.Core;

public sealed class SettingsManager
{
    private readonly string folderPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhyNoDPI");
    private string FilePath => Path.Combine(folderPath, "settings.json");

    public bool AutoStart { get; set; }
    public bool TelemetryEnabled { get; set; } = true;
    public string TelemetryServerUrl { get; set; } = string.Empty;
    public string ZapretDirectory { get; set; } = string.Empty;
    public string ZapretStrategyFile { get; set; } = "general.bat";

    public void Save()
    {
        Directory.CreateDirectory(folderPath);
        string tempPath = FilePath + ".tmp";
        string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, FilePath, overwrite: true);
    }

    public void Load()
    {
        if (!File.Exists(FilePath))
            return;

        try
        {
            SettingsManager? saved = JsonSerializer.Deserialize<SettingsManager>(File.ReadAllText(FilePath));
            if (saved is null)
                return;

            AutoStart = saved.AutoStart;
            TelemetryEnabled = saved.TelemetryEnabled;
            TelemetryServerUrl = saved.TelemetryServerUrl ?? string.Empty;
            ZapretDirectory = saved.ZapretDirectory ?? string.Empty;
            ZapretStrategyFile = string.IsNullOrWhiteSpace(saved.ZapretStrategyFile)
                ? "general.bat"
                : saved.ZapretStrategyFile;
        }
        catch (JsonException)
        {
            string backupPath = FilePath + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try { File.Copy(FilePath, backupPath, overwrite: false); } catch { }
        }
    }
}
