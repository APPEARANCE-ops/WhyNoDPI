using System;
using Microsoft.Win32;

namespace NoDPI.Core;

public sealed class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "WhyNoDPI";

    public void SetEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKey)
            ?? throw new InvalidOperationException("Не удалось открыть раздел автозапуска Windows.");

        if (!enabled)
        {
            key.DeleteValue(AppName, throwOnMissingValue: false);
            return;
        }

        string exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Не удалось определить путь к WhyNoDPI.exe.");
        key.SetValue(AppName, $"\"{exePath}\"", RegistryValueKind.String);
    }
}
