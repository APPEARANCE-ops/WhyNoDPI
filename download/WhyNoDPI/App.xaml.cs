using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using NoDPI.Views;

namespace NoDPI;

public partial class App : Application
{
    private Mutex? singleInstanceMutex;
    private bool ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        // Two engines fighting over WinDivert never ends well: keep one instance.
        singleInstanceMutex = new Mutex(initiallyOwned: true, @"Local\WhyNoDPI.SingleInstance", out bool isFirstInstance);
        ownsMutex = isFirstInstance;
        if (!isFirstInstance)
        {
            StartupUri = null;
            AppDialog.Warning(null, "WhyNoDPI уже запущен",
                "Приложение уже работает. Переключитесь на открытое окно WhyNoDPI — второй экземпляр запускать не нужно.");
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);
        e.Handled = true;
        try
        {
            AppDialog.Error(MainWindow, "Непредвиденная ошибка",
                e.Exception.Message +
                "\n\nПриложение продолжает работу. Подробности сохранены в журнале папки данных WhyNoDPI.");
        }
        catch
        {
            // A dialog must never be the reason for a second failure.
        }
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
            WriteCrashLog(exception);
    }

    private static void WriteCrashLog(Exception exception)
    {
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhyNoDPI", "logs");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            var builder = new StringBuilder();
            builder.AppendLine("WhyNoDPI unhandled exception");
            builder.AppendLine($"Time: {DateTime.Now:O}");
            builder.AppendLine($"Version: {typeof(App).Assembly.GetName().Version}");
            builder.AppendLine(new string('-', 60));
            builder.AppendLine(exception.ToString());
            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
        }
        catch
        {
            // Diagnostics must never throw.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (ownsMutex)
        {
            // Releasing a mutex we do not own throws; the second instance never owns it.
            try { singleInstanceMutex?.ReleaseMutex(); } catch { }
        }
        singleInstanceMutex?.Dispose();
        singleInstanceMutex = null;
        base.OnExit(e);
    }
}
