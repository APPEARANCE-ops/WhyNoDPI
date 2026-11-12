using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Shapes = System.Windows.Shapes;
using System.Windows.Threading;
using NoDPI.Core;
using NoDPI.Models;
using NoDPI.Views;

namespace NoDPI;

public partial class MainWindow : Window
{
    private enum EngineUiState
    {
        Stopped,
        Starting,
        Running,
        Stopping,
        Service,
        External
    }

    private readonly EngineManager engineManager = new();
    private readonly SettingsManager settings = new();
    private readonly StartupManager startupManager = new();
    private readonly NetworkDiagnostics diagnostics = new();
    private readonly TelemetryClient telemetry = new();

    private readonly List<LogEntry> allLogs = new();
    private readonly ObservableCollection<LogEntry> visibleLogs = new();
    private readonly ObservableCollection<LogEntry> recentLogs = new();
    private readonly ObservableCollection<StrategyTestResult> testResults = new();

    private readonly DispatcherTimer pollTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer uptimeTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private FrameworkElement? currentPage;
    private CancellationTokenSource? testCancellation;
    private EngineSnapshot snapshot = new(false, false, false, null);
    private EngineUiState uiState = EngineUiState.Stopped;
    private DateTime? engineStartedAt;
    private DateTime? lastServiceCheck;

    private bool uiReady;
    private bool isLoadingSettings = true;
    private bool isBusy;
    private bool isAutodetecting;
    private bool isCheckingServices;
    private bool allowClose;
    private int pollCounter;
    private int profileCount;
    private AppUpdateInfo? appUpdate;
    private ZapretUpdateInfo? zapretUpdate;
    private DateTime lastUpdateCheckUtc = DateTime.MinValue;
    private bool isCheckingUpdates;
    private string logFilter = "all";
    private string logSearch = string.Empty;

    public MainWindow()
    {
        InitializeComponent();

        LogList.ItemsSource = visibleLogs;
        RecentLogList.ItemsSource = recentLogs;
        TestResultsList.ItemsSource = testResults;

        uiReady = true;
        engineManager.StatusChanged += OnEngineStatusChanged;
        pollTimer.Tick += PollTimer_Tick;
        uptimeTimer.Tick += (_, _) => UpdateUptime();
        PreviewKeyDown += MainWindow_PreviewKeyDown;

        ShowPage(HomePage, NavHome);

#if USER_EDITION
        // Сборка «для пользователей»: адрес панели вшит при сборке и настраивать
        // его обычному пользователю не нужно — карточку убираем из настроек.
        TelemetryServerCard.Visibility = Visibility.Collapsed;
#endif
    }

    // =====================================================================
    //  Startup
    // =====================================================================
    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        isLoadingSettings = true;
        WindowAppearance.ApplyDarkTitleBar(this);

        string version = GetAppVersion();
        VersionText.Text = "v" + version;
        AppVersionText.Text = version;

        try
        {
            settings.Load();
        }
        catch (Exception ex)
        {
            AddLog("Не удалось загрузить настройки: " + ex.Message, LogLevel.Error);
        }

        try
        {
            startupManager.SetEnabled(settings.AutoStart);
        }
        catch (Exception ex)
        {
            AddLog("Не удалось применить автозапуск: " + ex.Message, LogLevel.Warning);
        }

        AutoStartBox.IsChecked = settings.AutoStart;
        TelemetryBox.IsChecked = settings.TelemetryEnabled;
        telemetry.CurrentStrategy = settings.ZapretStrategyFile;
        telemetry.ServerUrl = settings.TelemetryServerUrl;
        TelemetryUrlBox.Text = settings.TelemetryServerUrl;
        if (settings.TelemetryEnabled)
            telemetry.Start(settings.ZapretStrategyFile);
        UpdateTelemetryStatus();

        if (!string.IsNullOrWhiteSpace(settings.ZapretDirectory) && Directory.Exists(settings.ZapretDirectory))
        {
            try
            {
                string resolvedRoot = EngineManager.ResolveBundleRoot(settings.ZapretDirectory);
                if (!string.Equals(resolvedRoot, settings.ZapretDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    settings.ZapretDirectory = resolvedRoot;
                    SaveSettings();
                    AddLog("Автоматически найдена вложенная папка пакета Flowseal: " + resolvedRoot, LogLevel.Info);
                }
            }
            catch (Exception ex)
            {
                AddLog("Сохранённый путь Flowseal пока не прошёл проверку: " + ex.Message, LogLevel.Warning);
            }
        }

        engineManager.Configure(settings.ZapretDirectory);
        LoadStrategies(settings.ZapretStrategyFile);
        isLoadingSettings = false;

        AddLog("WhyNoDPI готов к работе.", LogLevel.Success);
        if (string.IsNullOrWhiteSpace(settings.ZapretDirectory))
            AddLog("Выберите папку Flowseal в настройках — без неё запуск движка невозможен.", LogLevel.Warning);

        bool alreadyRunning = engineManager.RefreshStatus();
        if (alreadyRunning)
            AddLog("Обнаружен уже работающий winws.exe из выбранной папки Flowseal.", LogLevel.Engine);

        await RefreshSnapshotAsync(queryService: true);
        UpdateUptime();

        pollTimer.Start();
        uptimeTimer.Start();

        if (!string.IsNullOrWhiteSpace(settings.ZapretDirectory))
            await RunServiceChecksAsync(silent: true);

        // Тихая проверка обновлений: если сеть недоступна, ничего не показываем.
        _ = CheckUpdatesAsync(manual: false);
    }

    private static string GetAppVersion()
    {
        try
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            return assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        }
        catch
        {
            return "1.0.0";
        }
    }

    // =====================================================================
    //  Navigation
    // =====================================================================
    private void NavHome_Checked(object sender, RoutedEventArgs e)
    {
        if (uiReady) ShowPage(HomePage, NavHome);
    }

    private void NavStrategies_Checked(object sender, RoutedEventArgs e)
    {
        if (uiReady) ShowPage(StrategiesPage, NavStrategies);
    }

    private void NavTelegram_Checked(object sender, RoutedEventArgs e)
    {
        if (uiReady) ShowPage(TelegramToolsPage, NavTelegram);
    }

    private void NavJournal_Checked(object sender, RoutedEventArgs e)
    {
        if (uiReady) ShowPage(JournalPage, NavJournal);
    }

    private void OpenJournal_Click(object sender, RoutedEventArgs e)
    {
        NavJournal.IsChecked = true;
    }

    private void NavSettings_Checked(object sender, RoutedEventArgs e)
    {
        if (uiReady) ShowPage(SettingsPage, NavSettings);
    }

    private void ShowPage(FrameworkElement page, RadioButton nav)
    {
        if (ReferenceEquals(currentPage, page))
            return;

        foreach (FrameworkElement candidate in new FrameworkElement[]
                 {
                     HomePage, StrategiesPage, TelegramToolsPage, JournalPage, SettingsPage
                 })
        {
            candidate.Visibility = ReferenceEquals(candidate, page) ? Visibility.Visible : Visibility.Collapsed;
        }

        currentPage = page;
        if (nav.IsChecked != true)
            nav.IsChecked = true;

        page.Opacity = 0;
        var transform = new TranslateTransform(0, 10);
        page.RenderTransform = transform;
        page.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)));
        transform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(230))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.D1:
                case Key.NumPad1:
                    NavHome.IsChecked = true;
                    e.Handled = true;
                    return;
                case Key.D2:
                case Key.NumPad2:
                    NavStrategies.IsChecked = true;
                    e.Handled = true;
                    return;
                case Key.D3:
                case Key.NumPad3:
                    NavTelegram.IsChecked = true;
                    e.Handled = true;
                    return;
                case Key.D4:
                case Key.NumPad4:
                    NavJournal.IsChecked = true;
                    e.Handled = true;
                    return;
                case Key.D5:
                case Key.NumPad5:
                    NavSettings.IsChecked = true;
                    e.Handled = true;
                    return;
            }
        }

        if (e.Key == Key.F5 && CheckServicesButton.IsEnabled)
        {
            CheckServices_Click(sender, e);
            e.Handled = true;
        }
    }

    // =====================================================================
    //  Engine state
    // =====================================================================
    private void OnEngineStatusChanged(string message)
    {
        Dispatcher.InvokeAsync(() => AddLog(message, LogLevel.Engine));
    }

    private async void PollTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            pollCounter++;
            // The Windows service check spawns sc.exe, so poll it less often.
            bool queryService = pollCounter % 4 == 1;
            await RefreshSnapshotAsync(queryService);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private async Task RefreshSnapshotAsync(bool queryService)
    {
        EngineSnapshot fresh = await Task.Run(() => engineManager.GetSnapshot(queryService));

        bool engineDied = snapshot.OwnedRunning && !fresh.OwnedRunning && uiState == EngineUiState.Running;
        snapshot = fresh;

        if (isAutodetecting)
            return;

        if (uiState is not (EngineUiState.Starting or EngineUiState.Stopping))
        {
            uiState = fresh.ServiceRunning ? EngineUiState.Service
                : fresh.OwnedRunning ? EngineUiState.Running
                : fresh.AnyWinwsProcess ? EngineUiState.External
                : EngineUiState.Stopped;
        }

        if (engineDied)
        {
            AddLog("Движок winws.exe завершился неожиданно.", LogLevel.Warning);
            ShowToast("Движок остановился. Проверьте журнал.", LogLevel.Warning);
            _ = RunServiceChecksAsync(silent: true);
        }

        ApplyUi();
    }

    private void SetUiState(EngineUiState state)
    {
        uiState = state;
        ApplyUi();
    }

    /// <summary>Re-renders every engine-dependent control from the current snapshot.</summary>
    private void ApplyUi()
    {
        bool owned = snapshot.OwnedRunning;
        bool service = snapshot.ServiceRunning;
        bool external = !owned && snapshot.AnyWinwsProcess;
        bool protectedTraffic = owned || service || external;
        bool externallyManaged = service || external;

        // Keep the derived state and the snapshot in sync so the headline and the
        // buttons can never contradict each other.
        if (uiState is not (EngineUiState.Starting or EngineUiState.Stopping))
        {
            uiState = service ? EngineUiState.Service
                : owned ? EngineUiState.Running
                : external ? EngineUiState.External
                : EngineUiState.Stopped;
        }

        if (owned)
            engineStartedAt ??= DateTime.Now;
        else
            engineStartedAt = null;

        bool hasProfiles = profileCount > 0;
        bool idle = !protectedTraffic && !isBusy && !isAutodetecting && !isCheckingServices;

        StrategyBox.IsEnabled = idle && hasProfiles;
        HomeStrategyBox.IsEnabled = idle && hasProfiles;
        StartSelectedStrategyButton.IsEnabled = idle && hasProfiles;
        AutoDetectButton.IsEnabled = idle && hasProfiles;
        AutoDetectButtonHome.IsEnabled = idle && hasProfiles;
        StopEngineButton.IsEnabled = owned && !isBusy && !isAutodetecting;
        ToggleButton.IsEnabled = !externallyManaged && !isBusy && !isAutodetecting;
        // The check button disables itself while a check is running; without this
        // line it would stay disabled forever after the first check.
        CheckServicesButton.IsEnabled = !isCheckingServices;

        ToggleButton.Style = (Style)FindResource(protectedTraffic ? "Btn.Danger" : "Btn.Primary");
        ToggleButton.Content = externallyManaged
            ? (service ? "Zapret работает как служба" : "Активен внешний Zapret")
            : owned ? "Остановить Zapret" : "Включить Zapret";

        (string title, string detail, Brush accent) = uiState switch
        {
            EngineUiState.Starting => ("Запускается…", "Подготовка профиля и запуск winws.exe.", Rgb(0x4D, 0x8D, 0xFF)),
            EngineUiState.Stopping => ("Останавливается…", "Завершение процесса winws.exe.", Rgb(0xF5, 0xB9, 0x42)),
            EngineUiState.Running => ("Защита включена",
                "winws.exe запущен напрямую. Командные файлы не используются.", Rgb(0x35, 0xD4, 0x99)),
            EngineUiState.Service => ("Служба Zapret",
                "Уже запущена служба Windows «zapret». Остановите её через средство управления Flowseal — второй движок не запускается.",
                Rgb(0xF5, 0xB9, 0x42)),
            EngineUiState.External => ("Внешний движок",
                "Найден winws.exe из другого расположения. Остановите его, чтобы WhyNoDPI управлял движком сам.",
                Rgb(0xF5, 0xB9, 0x42)),
            _ => ("Движок выключен",
                hasProfiles
                    ? "Готов к запуску выбранного профиля."
                    : "Выберите папку Flowseal в настройках, чтобы включить обход блокировок.",
                Rgb(0x68, 0x78, 0x91))
        };

        StatusText.Text = title;
        ModuleStatus.Text = detail;
        StatusDot.Fill = accent;
        HeaderStatusDot.Fill = accent;
        HeaderStatusText.Text = title;
        SidebarStatusDot.Fill = accent;

        SidebarStatusText.Text = protectedTraffic ? "Работает" : "Выключен";
        SidebarStatusText.Foreground = protectedTraffic ? Rgb(0x59, 0xD3, 0xA4) : Rgb(0x8B, 0x9B, 0xB5);

        string activeFile = engineManager.ActiveStrategyFile ?? settings.ZapretStrategyFile;
        string activeName = string.IsNullOrWhiteSpace(activeFile) ? "Не выбран" : StrategyManager.DisplayName(activeFile);
        CurrentStrategy.Text = activeName;
        HeroProfileText.Text = activeName;
        ActiveProfileFileText.Text = string.IsNullOrWhiteSpace(activeFile)
            ? "General"
            : Path.GetFileNameWithoutExtension(activeFile);

        SidebarEngineHint.Text = profileCount == 0
            ? "Профили не найдены"
            : $"{activeName} · {profileCount} профилей";

        StatusBarDot.Fill = accent;
        StatusBarText.Text = title;
        StatusBarText.Foreground = protectedTraffic ? Rgb(0xE9, 0xF0, 0xFB) : Rgb(0x8B, 0x9B, 0xB5);
        StatusBarProfile.Text = "Профиль: " + (string.IsNullOrWhiteSpace(activeFile)
            ? "не выбран"
            : StrategyManager.DisplayName(activeFile));

        UpdateUptime();
        UpdatePackageStatus();
    }

    private void UpdateUptime()
    {
        string checkedText = lastServiceCheck is null
            ? "Сервисы не проверялись"
            : "Сервисы проверены в " + lastServiceCheck.Value.ToString("HH:mm:ss");
        if (StatusBarCheck is not null)
            StatusBarCheck.Text = checkedText;

        if (engineStartedAt is null)
        {
            UptimeText.Text = lastServiceCheck is null
                ? string.Empty
                : "проверено " + lastServiceCheck.Value.ToString("HH:mm");
            return;
        }

        TimeSpan running = DateTime.Now - engineStartedAt.Value;
        UptimeText.Text = running.TotalHours >= 1
            ? $"работает {(int)running.TotalHours:00}:{running.Minutes:00}:{running.Seconds:00}"
            : $"работает {running.Minutes:00}:{running.Seconds:00}";
    }

    private void UpdatePackageStatus()
    {
        bool hasDirectory = !string.IsNullOrWhiteSpace(settings.ZapretDirectory) &&
                            Directory.Exists(settings.ZapretDirectory);
        bool hasEngine = hasDirectory && File.Exists(Path.Combine(settings.ZapretDirectory, "bin", "winws.exe"));
        bool hasDriver = hasDirectory && Directory.Exists(Path.Combine(settings.ZapretDirectory, "bin")) &&
                         Directory.GetFiles(Path.Combine(settings.ZapretDirectory, "bin"), "*.sys").Length > 0;
        bool isAdmin = IsRunningAsAdministrator();

        SetSummary(CheckDriverText, hasDriver);
        SetSummary(CheckProfilesText, profileCount > 0);
        SetSummary(CheckAdminText, isAdmin);

        if (!hasDirectory)
        {
            PackageStatusText.Text = "Пакет Flowseal не выбран. Скачайте архив, распакуйте и укажите папку.";
            PackageStatusText.Foreground = Rgb(0x8B, 0x9B, 0xB5);
            return;
        }

        if (hasEngine && hasDriver && profileCount > 0)
        {
            PackageStatusText.Text = $"Пакет в порядке: {profileCount} профилей, WinDivert найден.";
            PackageStatusText.Foreground = Rgb(0x6E, 0xD9, 0xB2);
            return;
        }

        PackageStatusText.Text = "Пакет неполный: " +
            (hasEngine ? string.Empty : "нет bin\\winws.exe. ") +
            (hasDriver ? string.Empty : "нет драйвера *.sys. ") +
            (profileCount > 0 ? string.Empty : "профили General не найдены.");
        PackageStatusText.Foreground = Rgb(0xF5, 0xB9, 0x42);
    }

    private static void SetSummary(TextBlock target, bool ok)
    {
        if (target is null)
            return;
        target.Text = ok ? "готово" : "нет";
        target.Foreground = ok ? Rgb(0x6E, 0xD9, 0xB2) : Rgb(0xF1, 0x9A, 0xA5);
    }

    private static bool IsRunningAsAdministrator()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static Brush Rgb(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));

    // =====================================================================
    //  Strategies
    // =====================================================================
    private void LoadStrategies(string preferredFile)
    {
        bool previous = isLoadingSettings;
        isLoadingSettings = true;
        try
        {
            StrategyBox.Items.Clear();
            HomeStrategyBox.Items.Clear();
            profileCount = 0;

            if (string.IsNullOrWhiteSpace(settings.ZapretDirectory) || !Directory.Exists(settings.ZapretDirectory))
            {
                AddStrategyPlaceholder("Пакет Flowseal не выбран");
                CurrentStrategy.Text = "Не выбран";
                ActiveProfileFileText.Text = "General";
                ProfileCountText.Text = "Профили не найдены";
                StrategyDescription.Text = "В настройках выберите корневую папку распакованного пакета Flowseal.";
                ZapretPathText.Text = "Папка Flowseal не выбрана";
                return;
            }

            IReadOnlyList<string> profiles = StrategyManager.FindProfiles(settings.ZapretDirectory);
            ZapretPathText.Text = settings.ZapretDirectory;
            profileCount = profiles.Count;
            ProfileCountText.Text = profiles.Count == 0
                ? "Профили не найдены"
                : $"Найдено профилей: {profiles.Count}";

            if (profiles.Count == 0)
            {
                AddStrategyPlaceholder("Профили General не найдены");
                CurrentStrategy.Text = "Нет профилей";
                ActiveProfileFileText.Text = "General";
                StrategyDescription.Text = "Проверьте, что выбрана папка Flowseal с профилями General, bin\\winws.exe и списками.";
                return;
            }

            PopulateStrategyBox(StrategyBox, profiles, preferredFile);
            PopulateStrategyBox(HomeStrategyBox, profiles, preferredFile);

            if (StrategyBox.SelectedItem is ComboBoxItem { Tag: string selectedFile })
            {
                settings.ZapretStrategyFile = selectedFile;
                CurrentStrategy.Text = StrategyManager.DisplayName(selectedFile);
                ActiveProfileFileText.Text = Path.GetFileNameWithoutExtension(selectedFile);
                StrategyDescription.Text =
                    $"Профиль «{StrategyManager.DisplayName(selectedFile)}» будет передан прямо в winws.exe.";
            }
        }
        finally
        {
            isLoadingSettings = previous;
        }
    }

    private void AddStrategyPlaceholder(string label)
    {
        StrategyBox.Items.Add(new ComboBoxItem { Content = label, IsEnabled = false, Tag = null });
        HomeStrategyBox.Items.Add(new ComboBoxItem { Content = label, IsEnabled = false, Tag = null });
        StrategyBox.SelectedIndex = 0;
        HomeStrategyBox.SelectedIndex = 0;
        profileCount = 0;
    }

    private static void PopulateStrategyBox(ComboBox box, IReadOnlyList<string> profiles, string preferredFile)
    {
        foreach (string fileName in profiles)
        {
            box.Items.Add(new ComboBoxItem
            {
                Content = StrategyManager.DisplayName(fileName),
                Tag = fileName
            });
        }

        ComboBoxItem? selected = box.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, preferredFile, StringComparison.OrdinalIgnoreCase));
        box.SelectedItem = selected ?? box.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private void HomeStrategyBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => HandleStrategySelection(HomeStrategyBox);

    private void StrategyBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => HandleStrategySelection(StrategyBox);

    private void HandleStrategySelection(ComboBox source)
    {
        if (isLoadingSettings || source.SelectedItem is not ComboBoxItem { Tag: string fileName })
            return;

        if (snapshot.AnyProtected)
        {
            string activeFile = engineManager.ActiveStrategyFile ?? settings.ZapretStrategyFile;
            SelectProfile(activeFile);
            StrategyDescription.Text = "Чтобы сменить профиль, сначала остановите текущий.";
            ShowToast("Остановите движок, чтобы сменить профиль.", LogLevel.Warning);
            return;
        }

        isLoadingSettings = true;
        try
        {
            SelectComboBoxProfile(StrategyBox, fileName);
            SelectComboBoxProfile(HomeStrategyBox, fileName);
        }
        finally
        {
            isLoadingSettings = false;
        }

        settings.ZapretStrategyFile = fileName;
        telemetry.CurrentStrategy = fileName;
        CurrentStrategy.Text = StrategyManager.DisplayName(fileName);
        HeroProfileText.Text = StrategyManager.DisplayName(fileName);
        ActiveProfileFileText.Text = Path.GetFileNameWithoutExtension(fileName);
        StrategyDescription.Text = $"Выбран профиль «{StrategyManager.DisplayName(fileName)}». Нажмите «Включить Zapret».";
        SaveSettings();
        AddLog("Выбран профиль: " + fileName, LogLevel.Info);
        ApplyUi();
    }

    private void SelectProfile(string fileName)
    {
        bool previous = isLoadingSettings;
        isLoadingSettings = true;
        try
        {
            SelectComboBoxProfile(StrategyBox, fileName);
            SelectComboBoxProfile(HomeStrategyBox, fileName);
        }
        finally
        {
            isLoadingSettings = previous;
        }

        CurrentStrategy.Text = StrategyManager.DisplayName(fileName);
        HeroProfileText.Text = StrategyManager.DisplayName(fileName);
        ActiveProfileFileText.Text = Path.GetFileNameWithoutExtension(fileName);
    }

    private static void SelectComboBoxProfile(ComboBox box, string fileName)
    {
        ComboBoxItem? match = box.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, fileName, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
            box.SelectedItem = match;
    }

    // =====================================================================
    //  Engine actions
    // =====================================================================
    private async void ToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (isBusy || isAutodetecting)
            return;

        await RefreshSnapshotAsync(queryService: true);
        if (snapshot.ServiceRunning || (!engineManager.RefreshStatus() && snapshot.AnyWinwsProcess))
        {
            AppDialog.Info(this, "Zapret уже работает",
                "Обнаружен уже работающий Zapret, запущенный не этим окном. WhyNoDPI не будет останавливать чужой процесс. Остановите его штатным способом и повторите попытку.");
            return;
        }

        if (snapshot.OwnedRunning)
        {
            await StopEngineAsync();
            return;
        }

        await StartSelectedAsync();
    }

    private async Task StopEngineAsync()
    {
        isBusy = true;
        SetUiState(EngineUiState.Stopping);
        AddLog("Остановка сетевого движка…", LogLevel.Engine);
        try
        {
            await engineManager.StopAsync();
            AddLog("Движок остановлен.", LogLevel.Success);
            ShowToast("Обход блокировок выключен.", LogLevel.Success);
            ResetServiceTiles();
        }
        catch (Exception ex)
        {
            AddLog("Ошибка при остановке движка: " + ex.Message, LogLevel.Error);
            AppDialog.Error(this, "Не удалось остановить движок", ex.Message);
        }
        finally
        {
            isBusy = false;
            await RefreshSnapshotAsync(queryService: true);
            if (!snapshot.OwnedRunning && uiState != EngineUiState.Service)
                SetUiState(EngineUiState.Stopped);
        }
    }

    private async Task<bool> StartSelectedAsync()
    {
        if (string.IsNullOrWhiteSpace(settings.ZapretDirectory))
        {
            NavSettings.IsChecked = true;
            AppDialog.Warning(this, "Пакет не выбран",
                "Сначала выберите папку распакованного пакета Flowseal в разделе «Настройки».");
            return false;
        }

        if (profileCount == 0)
        {
            AppDialog.Warning(this, "Профили не найдены",
                "В выбранной папке нет general-профилей Flowseal. Проверьте, что архив распакован полностью.");
            return false;
        }

        bool started;
        isBusy = true;
        SetUiState(EngineUiState.Starting);
        try
        {
            started = await engineManager.StartAsync(settings.ZapretDirectory, settings.ZapretStrategyFile);
        }
        catch (Exception ex)
        {
            AddLog("Ошибка запуска: " + ex.Message, LogLevel.Error);
            AppDialog.Error(this, "Ошибка запуска", ex.Message);
            started = false;
        }
        finally
        {
            isBusy = false;
        }

        await RefreshSnapshotAsync(queryService: true);

        if (!started)
        {
            string reason = engineManager.LastFailureMessage ??
                            "Не удалось запустить выбранный профиль. Откройте журнал и выполните диагностику пакета.";
            AddLog("Запуск не выполнен: " + reason, LogLevel.Error);
            SetUiState(EngineUiState.Stopped);
            AppDialog.Error(this, "Не удалось запустить Zapret", reason +
                (string.IsNullOrWhiteSpace(engineManager.LastLogPath) ? string.Empty : "\n\nЖурнал: " + engineManager.LastLogPath));
            return false;
        }

        SetUiState(EngineUiState.Running);
        ShowToast("Обход блокировок включён.", LogLevel.Success);
        await Task.Delay(900);

        int reachable = await RunServiceChecksAsync(silent: true);
        if (reachable >= 0 && reachable < 2)
        {
            // Движок поднялся, но выбранный профиль слабо помогает в этой сети —
            // честно об этом сказать и предложить автоподбор.
            AddLog($"Профиль {settings.ZapretStrategyFile} поднялся, но доступно сервисов: {reachable}/3. " +
                   "Рекомендуется автоподбор.", LogLevel.Warning);
            ModuleStatus.Text = "Движок работает, но сервисы почти не открываются. Попробуйте «Подобрать стратегию».";
            ShowToast("Профиль работает слабо — нажмите «Подобрать стратегию».", LogLevel.Warning);
        }

        return true;
    }

    private async void StartSelectedStrategy_Click(object sender, RoutedEventArgs e)
    {
        if (isBusy || isAutodetecting)
            return;

        await RefreshSnapshotAsync(queryService: false);
        if (snapshot.ServiceRunning || (!engineManager.RefreshStatus() && snapshot.AnyWinwsProcess))
        {
            AppDialog.Info(this, "Zapret уже работает",
                "Сначала остановите внешний Zapret или службу Flowseal. WhyNoDPI не должен перезапускать чужой winws.exe.");
            return;
        }

        if (snapshot.OwnedRunning)
            await StopEngineAsync();

        await StartSelectedAsync();
    }

    private async void StopEngine_Click(object sender, RoutedEventArgs e)
    {
        await RefreshSnapshotAsync(queryService: false);
        if (snapshot.ServiceRunning)
        {
            AppDialog.Info(this, "Служба Flowseal",
                "Zapret запущен как служба Windows. Остановите её через средство управления Flowseal.");
            return;
        }

        if (!snapshot.OwnedRunning)
            return;

        await StopEngineAsync();
    }

    // =====================================================================
    //  Automatic strategy detection
    // =====================================================================
    private async void AutoDetectButton_Click(object sender, RoutedEventArgs e)
    {
        if (isAutodetecting || isBusy)
            return;

        if (string.IsNullOrWhiteSpace(settings.ZapretDirectory) || !Directory.Exists(settings.ZapretDirectory))
        {
            NavSettings.IsChecked = true;
            AppDialog.Warning(this, "Папка не выбрана",
                "Сначала выберите распакованную папку Flowseal в настройках.");
            return;
        }

        await RefreshSnapshotAsync(queryService: true);
        if (snapshot.ServiceRunning || (!engineManager.RefreshStatus() && snapshot.AnyWinwsProcess))
        {
            AppDialog.Info(this, "Zapret уже работает",
                "Сначала остановите уже работающий Zapret вне WhyNoDPI. Автоподбор не должен запускать несколько движков одновременно.");
            return;
        }

        IReadOnlyList<string> profiles = StrategyManager.FindProfiles(settings.ZapretDirectory);
        if (profiles.Count == 0)
        {
            AppDialog.Warning(this, "Нет стратегий",
                "В выбранной папке не найдены профили General из пакета Flowseal.");
            return;
        }

        if (snapshot.OwnedRunning)
            await StopEngineAsync();

        NavStrategies.IsChecked = true;
        isAutodetecting = true;
        testCancellation?.Dispose();
        testCancellation = new CancellationTokenSource();
        CancellationToken token = testCancellation.Token;

        testResults.Clear();
        UpdateResultsState(profiles.Count);
        CancelTestButton.Visibility = Visibility.Visible;
        TestProgressBar.Value = 0;
        TestProgressPercent.Text = "0 %";
        TestProgressText.Text = "Подготовка…";
        SetServiceTilesPending("Идёт проверка стратегий…");
        ApplyUi();
        AddLog($"Начат автоматический подбор. Найдено профилей: {profiles.Count}.", LogLevel.Info);

        string? bestFile = null;
        int bestScore = 0;
        IReadOnlyList<ServiceCheckResult>? bestChecks = null;
        bool perfectMatch = false;

        try
        {
            for (int i = 0; i < profiles.Count; i++)
            {
                token.ThrowIfCancellationRequested();

                string fileName = profiles[i];
                string displayName = StrategyManager.DisplayName(fileName);
                TestProgressText.Text = $"Проверка {i + 1} из {profiles.Count}: {displayName}";
                ModuleStatus.Text = $"Тестируется «{displayName}»…";
                AddLog($"Тест профиля {fileName}", LogLevel.Info);

                bool started;
                try
                {
                    started = await engineManager.StartAsync(settings.ZapretDirectory, fileName);
                }
                catch (Exception ex)
                {
                    AddLog($"Не удалось запустить {fileName}: {ex.Message}", LogLevel.Error);
                    testResults.Insert(0, new StrategyTestResult(displayName, StrategyTestState.Failed,
                        "Ошибка запуска: " + ex.Message, 0, 3));
                    UpdateResultsState(profiles.Count);
                    continue;
                }

                if (!started)
                {
                    string hint = string.IsNullOrWhiteSpace(engineManager.LastLogPath)
                        ? "движок не запустился"
                        : "движок не запустился · журнал: " + engineManager.LastLogPath;
                    testResults.Insert(0, new StrategyTestResult(displayName, StrategyTestState.Failed, hint, 0, 3));
                    UpdateResultsState(profiles.Count);
                    continue;
                }

                // Give WinDivert and the profile a moment to apply to new connections.
                await Task.Delay(1600, token);
                IReadOnlyList<ServiceCheckResult> checks = await diagnostics.CheckImportantServicesAsync(token);
                int score = checks.Count(x => x.IsReachable);
                string summary = string.Join("  ·  ", checks.Select(x => $"{x.Service} {(x.IsReachable ? "✓" : "✕")}"));
                StrategyTestState state = score == checks.Count ? StrategyTestState.Passed
                    : score > 0 ? StrategyTestState.Partial
                    : StrategyTestState.Failed;

                testResults.Insert(0, new StrategyTestResult(displayName, state, summary, score, checks.Count));
                UpdateResultsState(profiles.Count);
                TestProgressBar.Value = (double)(i + 1) / profiles.Count * 100;
                TestProgressPercent.Text = $"{(int)TestProgressBar.Value} %";
                AddLog($"Результат {fileName}: {summary}; {score}/{checks.Count}.", state == StrategyTestState.Passed ? LogLevel.Success : LogLevel.Warning);

                if (score > bestScore)
                {
                    bestScore = score;
                    bestFile = fileName;
                    bestChecks = checks;
                }

                if (score == checks.Count)
                {
                    bestFile = fileName;
                    bestScore = score;
                    bestChecks = checks;
                    perfectMatch = true;
                    break;
                }

                await engineManager.StopAsync();
            }

            if (bestFile is null || bestScore == 0)
            {
                if (engineManager.RefreshStatus())
                    await engineManager.StopAsync();
                TestProgressText.Text = "Подходящий профиль не найден";
                ModuleStatus.Text = "Ни один профиль не открыл тестовые HTTPS-узлы. Проверьте WinDivert, антивирус и сеть.";
                SetServiceTilesPending("Доступность не подтверждена");
                AddLog("Автоподбор завершён: ни один профиль не прошёл проверку.", LogLevel.Error);
                AppDialog.Warning(this, "Подходящий профиль не найден",
                    "Ни один general-профиль не открыл YouTube, Discord и Telegram. Проверьте драйвер WinDivert, антивирус и параметры сети.");
                return;
            }

            if (!engineManager.RefreshStatus() ||
                !string.Equals(engineManager.ActiveStrategyFile, bestFile, StringComparison.OrdinalIgnoreCase))
            {
                if (engineManager.RefreshStatus())
                    await engineManager.StopAsync();
                bool restored = await engineManager.StartAsync(settings.ZapretDirectory, bestFile);
                if (!restored)
                    throw new InvalidOperationException("Найденный профиль прошёл тест, но повторно запустить его не удалось.");
            }

            SelectProfile(bestFile);
            settings.ZapretStrategyFile = bestFile;
            SaveSettings();
            ApplyServiceChecks(bestChecks!);
            TestProgressBar.Value = 100;
            TestProgressPercent.Text = "100 %";
            TestProgressText.Text = perfectMatch
                ? "Найден профиль с доступом ко всем сервисам"
                : $"Лучший результат: {bestScore}/{bestChecks!.Count}";
            AddLog($"Автоподбор завершён. Выбран {bestFile}, результат {bestScore}/{bestChecks!.Count}.",
                perfectMatch ? LogLevel.Success : LogLevel.Warning);
            ShowToast($"Выбран профиль «{StrategyManager.DisplayName(bestFile)}».", LogLevel.Success);
        }
        catch (OperationCanceledException)
        {
            AddLog("Автоподбор отменён пользователем.", LogLevel.Warning);
            TestProgressText.Text = "Отменено";
            try
            {
                if (engineManager.RefreshStatus())
                    await engineManager.StopAsync();
            }
            catch
            {
                // Cancellation must never throw.
            }
            SetServiceTilesPending("Проверка отменена");
            ShowToast("Автоподбор отменён.", LogLevel.Warning);
        }
        catch (Exception ex)
        {
            AddLog("Ошибка автоподбора: " + ex.Message, LogLevel.Error);
            TestProgressText.Text = "Ошибка проверки";
            AppDialog.Error(this, "Ошибка автоподбора", ex.Message);
        }
        finally
        {
            isAutodetecting = false;
            CancelTestButton.Visibility = Visibility.Collapsed;
            await RefreshSnapshotAsync(queryService: true);
            ApplyUi();
        }
    }

    private void CancelTest_Click(object sender, RoutedEventArgs e)
    {
        testCancellation?.Cancel();
        CancelTestButton.IsEnabled = false;
        TestProgressText.Text = "Отмена…";
    }

    private void UpdateResultsState(int profileCountInRun)
    {
        TestResultsEmpty.Visibility = testResults.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TestResultsCount.Text = profileCountInRun > 0
            ? $"{testResults.Count} из {profileCountInRun}"
            : $"{testResults.Count} профилей";
        CancelTestButton.IsEnabled = true;
    }

    // =====================================================================
    //  Service availability
    // =====================================================================
    private async void CheckServices_Click(object sender, RoutedEventArgs e)
    {
        await RunServiceChecksAsync(silent: false);
    }

    /// <summary>Возвращает число доступных сервисов или -1, если проверить не удалось.</summary>
    private async Task<int> RunServiceChecksAsync(bool silent)
    {
        if (isCheckingServices)
            return -1;

        isCheckingServices = true;
        CheckServicesButton.IsEnabled = false;
        SetServiceTilesPending(silent ? "Проверка…" : "Проверяем доступность…");
        try
        {
            IReadOnlyList<ServiceCheckResult> checks = await diagnostics.CheckImportantServicesAsync();
            ApplyServiceChecks(checks);
            lastServiceCheck = DateTime.Now;
            foreach (ServiceCheckResult check in checks)
                AddLog($"Проверка {check.Service}: {check.Details}",
                    check.IsReachable ? LogLevel.Success : LogLevel.Warning);
            UpdateUptime();
            return checks.Count(x => x.IsReachable);
        }
        catch (Exception ex)
        {
            AddLog("Проверка доступности не удалась: " + ex.Message, LogLevel.Error);
            SetServiceTilesPending("Проверка не удалась");
            return -1;
        }
        finally
        {
            isCheckingServices = false;
            ApplyUi();
        }
    }

    private void SetServiceTilesPending(string label)
    {
        Brush muted = Rgb(0x8B, 0x9B, 0xB5);
        foreach ((TextBlock text, Shapes.Ellipse dot) in new[] { (YoutubeStatus, YoutubeDot), (DiscordStatus, DiscordDot), (TelegramStatus, TelegramDot) })
        {
            text.Text = label;
            text.Foreground = muted;
            dot.Fill = Rgb(0x4D, 0x8D, 0xFF);
        }
    }

    private void ResetServiceTiles()
    {
        SetServiceTilesPending("Движок выключен — нажмите «Проверить»");
        foreach (Shapes.Ellipse dot in new[] { YoutubeDot, DiscordDot, TelegramDot })
            dot.Fill = Rgb(0x68, 0x78, 0x91);
    }

    private void ApplyServiceChecks(IReadOnlyList<ServiceCheckResult> checks)
    {
        Apply(check: checks.FirstOrDefault(x => x.Service == "YouTube"), text: YoutubeStatus, dot: YoutubeDot);
        Apply(check: checks.FirstOrDefault(x => x.Service == "Discord"), text: DiscordStatus, dot: DiscordDot);
        Apply(check: checks.FirstOrDefault(x => x.Service == "Telegram"), text: TelegramStatus, dot: TelegramDot);

        static void Apply(ServiceCheckResult? check, TextBlock text, Shapes.Ellipse dot)
        {
            if (check is null)
                return;
            text.Text = check.Details;
            text.Foreground = check.IsReachable ? Rgb(0x74, 0xE0, 0xB6) : Rgb(0xF1, 0x9A, 0xA5);
            dot.Fill = check.IsReachable ? Rgb(0x35, 0xD4, 0x99) : Rgb(0xF1, 0x64, 0x6F);
        }
    }

    // =====================================================================
    //  Settings
    // =====================================================================
    private void SelectZapret_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Выберите корневую папку распакованного пакета Flowseal",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
            return;

        string folder = dialog.FolderName;
        try
        {
            folder = EngineManager.ResolveBundleRoot(folder);
            IReadOnlyList<string> profiles = StrategyManager.FindProfiles(folder);
            if (profiles.Count == 0)
                throw new FileNotFoundException("В выбранной папке не найдены профили General из пакета Flowseal.");
            string script = Path.Combine(folder, profiles[0]);
            string executable = Path.Combine(folder, "bin", "winws.exe");
            EngineManager.ValidateBundle(folder, script, executable);

            if (engineManager.RefreshStatus() || snapshot.ServiceRunning || snapshot.AnyWinwsProcess)
            {
                AppDialog.Warning(this, "Движок работает",
                    "Перед сменой папки остановите текущий профиль или службу Flowseal.");
                return;
            }

            settings.ZapretDirectory = folder;
            settings.ZapretStrategyFile = profiles.Contains("general.bat", StringComparer.OrdinalIgnoreCase)
                ? "general.bat"
                : profiles[0];
            engineManager.Configure(folder);
            LoadStrategies(settings.ZapretStrategyFile);
            SaveSettings();
            AddLog("Выбран пакет Flowseal: " + folder, LogLevel.Success);
            ShowToast($"Пакет выбран · профилей: {profiles.Count}", LogLevel.Success);

            if (folder.Any(ch => ch > 127) || folder.Contains(' '))
                AddLog("Совет: для совместимости лучше распаковать Flowseal в путь без пробелов и кириллицы, например C:\\zapret-flowseal.",
                    LogLevel.Warning);

            ApplyUi();
            _ = RunServiceChecksAsync(silent: true);
        }
        catch (Exception ex)
        {
            AppDialog.Warning(this, "Папка Flowseal не подходит", ex.Message);
        }
    }

    private void DiagnosePackage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string report = EngineManager.BuildDiagnostics(settings.ZapretDirectory, settings.ZapretStrategyFile);
            AddLog("Выполнена проверка пакета Flowseal.", LogLevel.Info);
            foreach (string line in report.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                AddLog(line, LogLevel.Info);

            AppDialog.Show(this, "Диагностика пакета", report, DialogTone.Info,
                new[] { new DialogButton("Закрыть", primary: true) },
                subtitle: "Проверка прав, расположения файлов и запущенных процессов");
        }
        catch (Exception ex)
        {
            AddLog("Ошибка диагностики: " + ex.Message, LogLevel.Error);
            AppDialog.Warning(this, "Диагностика WhyNoDPI", ex.Message);
        }
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        string path = settings.ZapretDirectory;
        if (string.IsNullOrWhiteSpace(path))
        {
            ShowToast("Папка Flowseal ещё не выбрана.", LogLevel.Warning);
            return;
        }

        TryCopyToClipboard(path, "Путь к пакету скопирован.");
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        string folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhyNoDPI");
        OpenFolder(folder);
    }

    private void SettingChanged(object sender, RoutedEventArgs e)
    {
        if (isLoadingSettings)
            return;

        bool enabled = AutoStartBox.IsChecked == true;
        try
        {
            startupManager.SetEnabled(enabled);
            settings.AutoStart = enabled;
            SaveSettings();
            AddLog(enabled ? "Автозапуск WhyNoDPI включён." : "Автозапуск WhyNoDPI выключен.", LogLevel.Info);
            ShowToast(enabled ? "Автозапуск включён." : "Автозапуск выключен.", LogLevel.Success);
        }
        catch (Exception ex)
        {
            isLoadingSettings = true;
            AutoStartBox.IsChecked = settings.AutoStart;
            isLoadingSettings = false;
            AddLog("Не удалось изменить автозапуск: " + ex.Message, LogLevel.Error);
            AppDialog.Warning(this, "Настройка автозапуска", ex.Message);
        }
    }

    private void TelemetryChanged(object sender, RoutedEventArgs e)
    {
        if (isLoadingSettings)
            return;

        bool enabled = TelemetryBox.IsChecked == true;
        settings.TelemetryEnabled = enabled;
        SaveSettings();
        if (enabled)
        {
            telemetry.Start(settings.ZapretStrategyFile);
            AddLog("Анонимная статистика включена.", LogLevel.Info);
        }
        else
        {
            telemetry.Stop();
            AddLog("Анонимная статистика выключена.", LogLevel.Info);
        }
        UpdateTelemetryStatus();
    }

    private void SaveTelemetryUrl_Click(object sender, RoutedEventArgs e)
    {
        string raw = TelemetryUrlBox.Text.Trim();
        if (raw.Length == 0)
        {
            settings.TelemetryServerUrl = string.Empty;
            telemetry.ServerUrl = string.Empty;
            SaveSettings();
            telemetry.Stop();
            UpdateTelemetryStatus();
            ShowToast("Адрес панели очищен — статистика не отправляется.", LogLevel.Warning);
            return;
        }

        if (!TelemetryClient.TryNormalizeUrl(raw, out string normalized, out string error))
        {
            SetTelemetryStatus(error, LogLevel.Error);
            return;
        }

        settings.TelemetryServerUrl = normalized;
        telemetry.ServerUrl = normalized;
        TelemetryUrlBox.Text = normalized;
        SaveSettings();

        telemetry.Stop();
        if (settings.TelemetryEnabled)
            telemetry.Start(settings.ZapretStrategyFile);

        AddLog("Адрес панели статистики: " + normalized, LogLevel.Info);
        UpdateTelemetryStatus();
        ShowToast("Адрес панели сохранён.", LogLevel.Success);
    }

    private async void CheckTelemetryUrl_Click(object sender, RoutedEventArgs e)
    {
        string candidate = TelemetryUrlBox.Text.Trim();
        if (candidate.Length == 0)
        {
            SetTelemetryStatus("Сначала введите адрес панели.", LogLevel.Warning);
            return;
        }

        SetTelemetryStatus("Проверяем связь с панелью…", LogLevel.Info);
        (bool ok, string message) = await TelemetryClient.CheckAsync(candidate);
        SetTelemetryStatus(message, ok ? LogLevel.Success : LogLevel.Error);
        AddLog("Проверка панели статистики: " + message, ok ? LogLevel.Success : LogLevel.Warning);
    }

    private void UpdateTelemetryStatus()
    {
        if (!settings.TelemetryEnabled)
        {
            SetTelemetryStatus("Статистика отключена в настройках — данные не отправляются.", LogLevel.Warning);
            return;
        }

        if (!telemetry.IsConfigured)
        {
            SetTelemetryStatus("Панель не настроена — данные не отправляются.", LogLevel.Warning);
            return;
        }

        SetTelemetryStatus("Отправка включена: " + telemetry.ActiveServerUrl, LogLevel.Success);
    }

    private void SetTelemetryStatus(string message, LogLevel level)
    {
        if (TelemetryUrlStatus is null)
            return;

        TelemetryUrlStatus.Text = message;
        TelemetryUrlStatus.ToolTip = message;
        TelemetryUrlStatus.Foreground = level switch
        {
            LogLevel.Success => Rgb(0x74, 0xE0, 0xB6),
            LogLevel.Error => Rgb(0xF1, 0x9A, 0xA5),
            LogLevel.Warning => Rgb(0xF3, 0xC4, 0x74),
            _ => Rgb(0x8B, 0x9B, 0xB5)
        };
    }

    private void SaveSettings()
    {
        try
        {
            settings.Save();
        }
        catch (Exception ex)
        {
            AddLog("Не удалось сохранить настройки: " + ex.Message, LogLevel.Error);
        }
    }

    // =====================================================================
    //  Telegram / VPN
    // =====================================================================
    private void OpenTelegramProxyChannel_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl("https://t.me/mtp4tg");
        SetTelegramStatus(
            "Открыт канал @mtp4tg. Выбери запись с отметкой Online и нажми Connect в Telegram.",
            LogLevel.Info, warn: true);
        AddLog("Открыт канал MTProxy for Telegram (@mtp4tg).", LogLevel.Info);
    }

    private void ImportTelegramProxyLink_Click(object sender, RoutedEventArgs e)
    {
        if (!TelegramProxyLinkHelper.TryParse(TelegramProxyLinkBox.Text, out string server, out int port,
                out string secret, out string error))
        {
            SetTelegramStatus(error, LogLevel.Error, warn: true);
            return;
        }

        TelegramProxyServerBox.Text = server;
        TelegramProxyPortBox.Text = port.ToString();
        TelegramProxySecretBox.Password = secret;
        SetTelegramStatus(
            $"Параметры импортированы: {server}:{port}. Нажми «Подключить в Telegram» для подтверждения в клиенте.",
            LogLevel.Success, warn: false);
        AddLog($"Импортированы параметры MTProto-прокси для {server}:{port}; секрет не записывается в журнал.",
            LogLevel.Success);
    }

    private void ConnectTelegramProxy_Click(object sender, RoutedEventArgs e)
    {
        string server = TelegramProxyServerBox.Text.Trim();
        string portText = TelegramProxyPortBox.Text.Trim();
        string secret = TelegramProxySecretBox.Password.Trim();

        if (!int.TryParse(portText, out int port))
        {
            SetTelegramStatus("Порт должен быть числом от 1 до 65535.", LogLevel.Error, warn: true);
            return;
        }

        if (!TelegramProxyLinkHelper.TryBuild(server, port, secret, out string telegramUri, out string error))
        {
            SetTelegramStatus(error, LogLevel.Error, warn: true);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(telegramUri) { UseShellExecute = true });
            SetTelegramStatus("Ссылка передана Telegram. Подтверди подключение в открывшемся окне Telegram.",
                LogLevel.Success, warn: false);
            AddLog($"Открыта ссылка добавления MTProto-прокси {server}:{port}; secret не журналируется.",
                LogLevel.Success);
        }
        catch (Exception ex)
        {
            // Browser deep-link is a fallback when the Telegram tg:// handler is not registered.
            try
            {
                Process.Start(new ProcessStartInfo(TelegramProxyLinkHelper.ToWebLink(server, port, secret))
                    { UseShellExecute = true });
                SetTelegramStatus(
                    "Обработчик tg:// не зарегистрирован. Открыта ссылка t.me/proxy в браузере — выбери «Open in Telegram».",
                    LogLevel.Warning, warn: true);
                AddLog("Ссылка tg://proxy не открылась; использован HTTPS fallback t.me/proxy.", LogLevel.Warning);
            }
            catch (Exception fallbackEx)
            {
                SetTelegramStatus("Не удалось открыть Telegram: " + fallbackEx.Message, LogLevel.Error, warn: true);
                AddLog("Не удалось открыть ссылку MTProto: " + ex.Message + " / " + fallbackEx.Message,
                    LogLevel.Error);
            }
        }
    }

    private void ClearTelegramProxy_Click(object sender, RoutedEventArgs e)
    {
        TelegramProxyLinkBox.Clear();
        TelegramProxyServerBox.Clear();
        TelegramProxyPortBox.Clear();
        TelegramProxySecretBox.Clear();
        SetTelegramStatus("Поля очищены. Секрет прокси не сохраняется в настройках WhyNoDPI.",
            LogLevel.Info, warn: false);
        TelegramProxyLinkBox.Focus();
    }

    private void SetTelegramStatus(string message, LogLevel level, bool warn)
    {
        TelegramProxyStatusText.Text = message;
        TelegramProxyStatusText.Foreground = level switch
        {
            LogLevel.Success => Rgb(0x74, 0xE0, 0xB6),
            LogLevel.Error => Rgb(0xF1, 0x9A, 0xA5),
            LogLevel.Warning => Rgb(0xF3, 0xC4, 0x74),
            _ => Rgb(0x8B, 0x9B, 0xB5)
        };
        TelegramProxyStatusBanner.Background = warn ? Rgb(0x14, 0x1D, 0x2D) : Rgb(0x11, 0x1C, 0x2C);
    }

    private void OpenWindowsVpnSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:network-vpn") { UseShellExecute = true });
            AddLog("Открыты системные настройки VPN Windows.", LogLevel.Info);
        }
        catch (Exception ex)
        {
            AddLog("Не удалось открыть настройки VPN Windows: " + ex.Message, LogLevel.Error);
            AppDialog.Warning(this, "Настройки VPN", ex.Message);
        }
    }

    private void RefreshVpnStatus_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            NetworkInterface[] candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                    (adapter.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp ||
                     adapter.Name.Contains("WireGuard", StringComparison.OrdinalIgnoreCase) ||
                     adapter.Name.Contains("Wintun", StringComparison.OrdinalIgnoreCase) ||
                     adapter.Name.Contains("TAP", StringComparison.OrdinalIgnoreCase) ||
                     adapter.Description.Contains("WireGuard", StringComparison.OrdinalIgnoreCase) ||
                     adapter.Description.Contains("OpenVPN", StringComparison.OrdinalIgnoreCase) ||
                     adapter.Description.Contains("VPN", StringComparison.OrdinalIgnoreCase)))
                .ToArray();

            if (candidates.Length == 0)
            {
                VpnStatusText.Text = "Активный VPN-подобный интерфейс не обнаружен. Это не исключает VPN внутри отдельных приложений.";
                VpnStatusText.Foreground = Rgb(0xF3, 0xC4, 0x74);
                VpnStatusBanner.Background = Rgb(0x2C, 0x24, 0x13);
            }
            else
            {
                string names = string.Join(", ", candidates.Select(adapter => adapter.Name).Distinct().Take(5));
                VpnStatusText.Text = "Активные туннельные интерфейсы: " + names +
                                     ". Это признак адаптера, а не гарантия, что весь трафик идёт через него.";
                VpnStatusText.Foreground = Rgb(0x74, 0xE0, 0xB6);
                VpnStatusBanner.Background = Rgb(0x10, 0x2E, 0x26);
            }

            AddLog("Проверены сетевые интерфейсы Windows для поиска VPN/туннелей.", LogLevel.Info);
        }
        catch (Exception ex)
        {
            VpnStatusText.Text = "Не удалось проверить сетевые интерфейсы: " + ex.Message;
            VpnStatusText.Foreground = Rgb(0xF1, 0x9A, 0xA5);
            VpnStatusBanner.Background = Rgb(0x2E, 0x16, 0x20);
        }
    }

    // =====================================================================
    //  Launching services
    // =====================================================================
    private async void OpenYoutube_Click(object sender, RoutedEventArgs e) =>
        await OpenServiceAsync("https://www.youtube.com", "YouTube");

    private async void OpenTelegram_Click(object sender, RoutedEventArgs e) =>
        await OpenServiceAsync("https://web.telegram.org", "Telegram Web");

    private async Task OpenServiceAsync(string url, string serviceName)
    {
        try
        {
            bool bypassActive = snapshot.AnyProtected || engineManager.RefreshStatus();
            if (!bypassActive)
            {
                if (string.IsNullOrWhiteSpace(settings.ZapretDirectory))
                {
                    NavSettings.IsChecked = true;
                    AppDialog.Warning(this, "Пакет не выбран",
                        "Сначала выберите пакет Flowseal в настройках.");
                    return;
                }

                bool started = await StartSelectedAsync();
                if (!started)
                    return;
                await Task.Delay(700);
            }

            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            AddLog($"Открыт {serviceName}.", LogLevel.Info);
        }
        catch (Exception ex)
        {
            AddLog($"Не удалось открыть {serviceName}: {ex.Message}", LogLevel.Error);
            AppDialog.Error(this, "Ошибка открытия сайта", ex.Message);
        }
    }

    private async void OpenDiscord_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // При наличии уже работающего Flowseal/Zapret не запускаем второй winws.
            bool bypassActive = snapshot.AnyProtected || engineManager.RefreshStatus();
            if (!bypassActive)
            {
                if (string.IsNullOrWhiteSpace(settings.ZapretDirectory) || !Directory.Exists(settings.ZapretDirectory))
                {
                    DiscordStatus.Text = "Сначала выберите пакет Flowseal";
                    DiscordStatus.Foreground = Rgb(0xF3, 0xC4, 0x74);
                    NavSettings.IsChecked = true;
                    AppDialog.Warning(this, "Пакет Flowseal не выбран",
                        "Сначала выберите корневую папку распакованного пакета Flowseal в разделе «Настройки».");
                    return;
                }

                DiscordStatus.Text = "Запускается выбранный профиль…";
                DiscordStatus.Foreground = Rgb(0xF3, 0xC4, 0x74);
                bool started = await StartSelectedAsync();
                if (!started)
                {
                    DiscordStatus.Text = "Zapret не запущен — Discord не открыт";
                    DiscordStatus.Foreground = Rgb(0xF1, 0x9A, 0xA5);
                    AddLog("Discord не запущен: выбранный профиль не стартовал. Журнал: " +
                           (engineManager.LastLogPath ?? "не создан"), LogLevel.Error);
                    return;
                }

                await Task.Delay(900);
                if (!engineManager.RefreshStatus())
                {
                    DiscordStatus.Text = "Движок остановился при запуске";
                    DiscordStatus.Foreground = Rgb(0xF1, 0x9A, 0xA5);
                    AddLog("winws.exe завершился до запуска Discord.", LogLevel.Error);
                    return;
                }
            }

            (bool success, string details) = await TryLaunchDiscordDesktopAsync();
            if (success)
            {
                DiscordStatus.Text = "Приложение Discord открыто";
                DiscordStatus.Foreground = Rgb(0x74, 0xE0, 0xB6);
                DiscordDot.Fill = Rgb(0x35, 0xD4, 0x99);
                AddLog("Discord Desktop запущен: " + details, LogLevel.Success);
                return;
            }

            DiscordStatus.Text = "Не удалось запустить приложение Discord";
            DiscordStatus.Foreground = Rgb(0xF1, 0x9A, 0xA5);
            AddLog("Discord Desktop не найден или не запустился: " + details, LogLevel.Warning);
            bool openDownload = AppDialog.Confirm(this, "Discord не запущен",
                "Не удалось запустить установленное приложение Discord.\n\nОткрыть официальную страницу загрузки?",
                "Открыть discord.com", "Закрыть");
            if (openDownload)
                OpenUrl("https://discord.com/download");
        }
        catch (Exception ex)
        {
            AddLog("Не удалось запустить Discord Desktop: " + ex.Message, LogLevel.Error);
            AppDialog.Error(this, "Ошибка запуска Discord", ex.Message);
        }
    }

    private static async Task<(bool Success, string Details)> TryLaunchDiscordDesktopAsync()
    {
        string[] processNames = { "Discord", "DiscordPTB", "DiscordCanary" };
        if (IsProcessRunning(processNames))
            return (true, "Discord уже запущен");

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] installFolders = { "Discord", "DiscordPTB", "DiscordCanary" };
        var attempted = new List<string>();

        foreach (string folderName in installFolders)
        {
            string installRoot = Path.Combine(localAppData, folderName);
            string processExe = folderName switch
            {
                "DiscordPTB" => "DiscordPTB.exe",
                "DiscordCanary" => "DiscordCanary.exe",
                _ => "Discord.exe"
            };
            string processName = Path.GetFileNameWithoutExtension(processExe);
            string updater = Path.Combine(installRoot, "Update.exe");

            if (File.Exists(updater))
            {
                attempted.Add(updater);
                if (TryStartUnelevated(updater, $"--processStart {processExe}", installRoot) &&
                    await WaitForProcessAsync(processName, TimeSpan.FromSeconds(10)))
                    return (true, updater + " → " + processExe);
            }

            if (Directory.Exists(installRoot))
            {
                string[] appDirectories;
                try
                {
                    appDirectories = Directory.GetDirectories(installRoot, "app-*", SearchOption.TopDirectoryOnly)
                        .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                }
                catch
                {
                    appDirectories = Array.Empty<string>();
                }

                foreach (string appDirectory in appDirectories)
                {
                    string executable = Path.Combine(appDirectory, processExe);
                    attempted.Add(executable);
                    if (TryStartUnelevated(executable, string.Empty, appDirectory) &&
                        await WaitForProcessAsync(processName, TimeSpan.FromSeconds(8)))
                        return (true, executable);
                }
            }
        }

        string[] fixedPaths =
        {
            Path.Combine(localAppData, "Programs", "Discord", "Discord.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Discord", "Discord.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Discord", "Discord.exe")
        };
        foreach (string executable in fixedPaths)
        {
            attempted.Add(executable);
            if (TryStartUnelevated(executable, string.Empty, Path.GetDirectoryName(executable) ?? localAppData) &&
                await WaitForProcessAsync("Discord", TimeSpan.FromSeconds(8)))
                return (true, executable);
        }

        // URI-протокол полезен для Microsoft Store и других установок Discord.
        attempted.Add("discord:// protocol");
        if (TryStartUnelevated("discord://", string.Empty, localAppData) &&
            await WaitForProcessAsync("Discord", TimeSpan.FromSeconds(8)))
            return (true, "discord:// protocol");

        return (false, string.Join("; ", attempted));
    }

    private static bool TryStartUnelevated(string target, string arguments, string workingDirectory)
    {
        try
        {
            // WhyNoDPI запрашивает UAC для WinDivert. Передача запуска через
            // Explorer позволяет Discord открываться как обычное desktop-приложение,
            // а не наследовать повышенный токен WhyNoDPI.
            string explorer = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            string command = "\"" + target + "\"";
            if (!string.IsNullOrWhiteSpace(arguments))
                command += " " + arguments;

            Process.Start(new ProcessStartInfo
            {
                FileName = explorer,
                Arguments = command,
                WorkingDirectory = workingDirectory,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            return true;
        }
        catch
        {
            // Резервный вариант для систем, где Explorer не принимает команду запуска.
            try
            {
                var info = new ProcessStartInfo
                {
                    FileName = target,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = true
                };
                if (!string.IsNullOrWhiteSpace(arguments))
                    info.Arguments = arguments;
                Process.Start(info);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private static async Task<bool> WaitForProcessAsync(string processName, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (IsProcessRunning(new[] { processName }))
                return true;
            await Task.Delay(250);
        }
        return IsProcessRunning(new[] { processName });
    }

    private static bool IsProcessRunning(IEnumerable<string> processNames)
    {
        foreach (string processName in processNames)
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(processName); }
            catch { continue; }

            bool found = processes.Length > 0;
            foreach (Process process in processes)
                process.Dispose();
            if (found)
                return true;
        }
        return false;
    }

    private void OpenFlowseal_Click(object sender, RoutedEventArgs e) =>
        OpenUrl("https://github.com/Flowseal/zapret-discord-youtube");

    private void OpenFlowsealRelease_Click(object sender, RoutedEventArgs e) =>
        OpenUrl("https://github.com/Flowseal/zapret-discord-youtube/releases/latest");

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AddLog("Не удалось открыть ссылку: " + ex.Message, LogLevel.Error);
            AppDialog.Warning(this, "WhyNoDPI", ex.Message);
        }
    }

    private void OpenFolder(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AddLog("Не удалось открыть папку: " + ex.Message, LogLevel.Error);
            AppDialog.Warning(this, "WhyNoDPI", ex.Message);
        }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        OpenFolder(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhyNoDPI", "logs"));
    }

    // =====================================================================
    //  Updates
    // =====================================================================
    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        await CheckUpdatesAsync(manual: true);
    }

    private async Task CheckUpdatesAsync(bool manual)
    {
        if (isCheckingUpdates)
            return;

        // Автоматическую проверку не чаще раза в 30 минут; ручную — всегда.
        if (!manual && DateTime.UtcNow - lastUpdateCheckUtc < TimeSpan.FromMinutes(30) && appUpdate is not null)
            return;

        isCheckingUpdates = true;
        CheckUpdatesButton.IsEnabled = false;
        if (manual)
        {
            AppUpdateStatus.Text = "проверяем…";
            AppUpdateStatus.Foreground = Rgb(0x8B, 0x9B, 0xB5);
            ZapretUpdateStatus.Text = "проверяем…";
            ZapretUpdateStatus.Foreground = Rgb(0x8B, 0x9B, 0xB5);
        }

        try
        {
            string version = GetAppVersion();
            Task<AppUpdateInfo?> appTask = UpdateChecker.CheckAppAsync(telemetry.ActiveServerUrl, version);
            Task<ZapretUpdateInfo?> zapretTask = UpdateChecker.CheckZapretAsync(settings.ZapretDirectory);

            await Task.WhenAll(appTask, zapretTask);
            appUpdate = appTask.Result;
            zapretUpdate = zapretTask.Result;
            lastUpdateCheckUtc = DateTime.UtcNow;

            ApplyUpdateUi();

            bool appNewer = appUpdate?.IsNewer == true;
            bool zapretNewer = zapretUpdate?.IsNewer == true;

            if (appNewer)
                ShowToast($"Доступна новая версия WhyNoDPI {appUpdate!.LatestVersion}.", LogLevel.Success);
            if (zapretNewer)
                ShowToast("Для пакета Flowseal вышло обновление.", LogLevel.Success);

            if (manual)
            {
                string message = BuildUpdateSummary(appNewer, zapretNewer);
                AddLog("Проверка обновлений: " + message, LogLevel.Info);
                if (!appNewer && !zapretNewer)
                    ShowToast("Обновлений нет.", LogLevel.Success);
            }
        }
        catch (Exception ex)
        {
            AddLog("Проверка обновлений не удалась: " + ex.Message, LogLevel.Warning);
            if (manual)
                ShowToast("Не удалось проверить обновления.", LogLevel.Warning);
        }
        finally
        {
            isCheckingUpdates = false;
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private string BuildUpdateSummary(bool appNewer, bool zapretNewer)
    {
        if (!appNewer && !zapretNewer)
            return "обновлений нет";
        if (appNewer && zapretNewer)
            return $"WhyNoDPI {appUpdate?.LatestVersion}; новый пакет Flowseal";
        if (appNewer)
            return $"WhyNoDPI {appUpdate?.LatestVersion}";
        return "новый пакет Flowseal";
    }

    private void ApplyUpdateUi()
    {
        string version = GetAppVersion();

        if (appUpdate is null)
        {
            bool configured = telemetry.IsConfigured;
            AppUpdateStatus.Text = configured ? "не удалось проверить" : "панель не настроена";
            AppUpdateStatus.Foreground = Rgb(0x8B, 0x9B, 0xB5);
            AppUpdateButton.Visibility = Visibility.Collapsed;
        }
        else if (appUpdate.IsNewer)
        {
            AppUpdateStatus.Text = "доступна " + appUpdate.LatestVersion;
            AppUpdateStatus.Foreground = Rgb(0xF5, 0xB9, 0x42);
            AppUpdateButton.Visibility = string.IsNullOrWhiteSpace(appUpdate.DownloadUrl)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
        else
        {
            AppUpdateStatus.Text = "актуальная (" + version + ")";
            AppUpdateStatus.Foreground = Rgb(0x6E, 0xD9, 0xB2);
            AppUpdateButton.Visibility = Visibility.Collapsed;
        }

        if (zapretUpdate is null)
        {
            ZapretUpdateStatus.Text = string.IsNullOrWhiteSpace(settings.ZapretDirectory)
                ? "пакет не выбран"
                : "не удалось проверить";
            ZapretUpdateStatus.Foreground = Rgb(0x8B, 0x9B, 0xB5);
            ZapretUpdateButton.Visibility = Visibility.Collapsed;
        }
        else if (zapretUpdate.IsNewer)
        {
            ZapretUpdateStatus.Text = "доступен от " + zapretUpdate.PublishedUtc.ToLocalTime().ToString("dd.MM.yyyy");
            ZapretUpdateStatus.Foreground = Rgb(0xF5, 0xB9, 0x42);
            ZapretUpdateButton.Visibility = Visibility.Visible;
        }
        else
        {
            ZapretUpdateStatus.Text = "актуальный";
            ZapretUpdateStatus.Foreground = Rgb(0x6E, 0xD9, 0xB2);
            ZapretUpdateButton.Visibility = Visibility.Collapsed;
        }
    }

    private void OpenAppDownload_Click(object sender, RoutedEventArgs e)
    {
        string? url = appUpdate?.DownloadUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            ShowToast("Ссылка на новую версию не указана на сервере.", LogLevel.Warning);
            return;
        }

        OpenUrl(url);
        AddLog("Открыта страница загрузки WhyNoDPI: " + url, LogLevel.Info);
    }
    // =====================================================================
    //  Journal
    // =====================================================================
    private void AddLog(string message, LogLevel level = LogLevel.Info)
    {
        if (LogList is null || string.IsNullOrWhiteSpace(message))
            return;

        var entry = new LogEntry(level, message);
        allLogs.Insert(0, entry);
        while (allLogs.Count > 800)
            allLogs.RemoveAt(allLogs.Count - 1);

        if (MatchesFilter(entry))
            visibleLogs.Insert(0, entry);
        while (visibleLogs.Count > 500)
            visibleLogs.RemoveAt(visibleLogs.Count - 1);

        recentLogs.Insert(0, entry);
        while (recentLogs.Count > 5)
            recentLogs.RemoveAt(recentLogs.Count - 1);
        if (RecentLogEmpty is not null)
            RecentLogEmpty.Visibility = Visibility.Collapsed;

        UpdateLogEmptyState();
    }

    private bool MatchesFilter(LogEntry entry)
    {
        if (logFilter == "warn" && entry.Level is not (LogLevel.Warning or LogLevel.Error))
            return false;
        if (logFilter == "error" && entry.Level != LogLevel.Error)
            return false;

        if (string.IsNullOrWhiteSpace(logSearch))
            return true;

        return entry.Message.Contains(logSearch, StringComparison.OrdinalIgnoreCase) ||
               entry.TimeText.Contains(logSearch, StringComparison.OrdinalIgnoreCase);
    }

    private void RebuildVisibleLogs()
    {
        visibleLogs.Clear();
        foreach (LogEntry entry in allLogs)
        {
            if (!MatchesFilter(entry))
                continue;
            visibleLogs.Add(entry);
            if (visibleLogs.Count >= 500)
                break;
        }
        UpdateLogEmptyState();
    }

    private void UpdateLogEmptyState()
    {
        if (LogEmptyState is null)
            return;
        LogEmptyState.Visibility = visibleLogs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LogCountText.Text = allLogs.Count == 0
            ? "0 записей"
            : visibleLogs.Count == allLogs.Count
                ? $"{allLogs.Count} записей"
                : $"{visibleLogs.Count} из {allLogs.Count} записей";
    }

    private void LogFilter_Checked(object sender, RoutedEventArgs e)
    {
        if (!uiReady)
            return;

        logFilter = LogFilterError?.IsChecked == true ? "error"
            : LogFilterWarn?.IsChecked == true ? "warn"
            : "all";
        RebuildVisibleLogs();
    }

    private void LogSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!uiReady)
            return;

        logSearch = LogSearchBox.Text.Trim();
        if (LogSearchPlaceholder is not null)
            LogSearchPlaceholder.Visibility = logSearch.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RebuildVisibleLogs();
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        if (visibleLogs.Count == 0)
        {
            ShowToast("Журнал пуст.", LogLevel.Warning);
            return;
        }

        string text = string.Join(Environment.NewLine,
            visibleLogs.Reverse().Select(entry => $"[{entry.TimeText}] {entry.Message}"));
        TryCopyToClipboard(text, $"Скопировано записей: {visibleLogs.Count}.");
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        allLogs.Clear();
        visibleLogs.Clear();
        recentLogs.Clear();
        if (RecentLogEmpty is not null)
            RecentLogEmpty.Visibility = Visibility.Visible;
        AddLog("Журнал очищен.", LogLevel.Info);
    }

    private void TryCopyToClipboard(string text, string successMessage)
    {
        try
        {
            Clipboard.SetText(text);
            ShowToast(successMessage, LogLevel.Success);
        }
        catch (Exception ex)
        {
            AddLog("Не удалось скопировать в буфер обмена: " + ex.Message, LogLevel.Error);
            ShowToast("Буфер обмена недоступен.", LogLevel.Error);
        }
    }

    // =====================================================================
    //  Toasts
    // =====================================================================
    private void ShowToast(string message, LogLevel level = LogLevel.Info)
    {
        if (ToastStack is null)
            return;

        Brush accent = level switch
        {
            LogLevel.Success => Rgb(0x35, 0xD4, 0x99),
            LogLevel.Warning => Rgb(0xF5, 0xB9, 0x42),
            LogLevel.Error => Rgb(0xF1, 0x64, 0x6F),
            _ => Rgb(0x4D, 0x8D, 0xFF)
        };

        var toast = new Border
        {
            Background = Rgb(0x14, 0x20, 0x33),
            BorderBrush = accent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(13, 10, 13, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Opacity = 0,
            RenderTransform = new TranslateTransform(0, 12),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 4,
                Direction = 270,
                Color = Colors.Black,
                Opacity = 0.5,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance
            }
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var dot = new Shapes.Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = accent,
            Margin = new Thickness(0, 6, 10, 0),
            VerticalAlignment = VerticalAlignment.Top
        };
        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11.5,
            Foreground = Rgb(0xE2, 0xEA, 0xF7)
        };

        grid.Children.Add(dot);
        grid.Children.Add(text);
        Grid.SetColumn(text, 1);
        toast.Child = grid;

        ToastStack.Children.Insert(0, toast);
        while (ToastStack.Children.Count > 4)
            ToastStack.Children.RemoveAt(ToastStack.Children.Count - 1);

        toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)));
        ((TranslateTransform)toast.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

        _ = DismissToastAsync(toast);
    }

    private async Task DismissToastAsync(Border toast)
    {
        await Task.Delay(3800);
        if (!ToastStack.Children.Contains(toast))
            return;

        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(260));
        fade.Completed += (_, _) =>
        {
            if (ToastStack.Children.Contains(toast))
                ToastStack.Children.Remove(toast);
        };
        toast.BeginAnimation(OpacityProperty, fade);
    }

    // =====================================================================
    //  Window lifetime
    // =====================================================================
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowAppearance.ApplyDarkTitleBar(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        pollTimer.Stop();
        uptimeTimer.Stop();
        telemetry.Stop();
        testCancellation?.Cancel();
        base.OnClosed(e);
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (allowClose || !snapshot.OwnedRunning || snapshot.ServiceRunning)
            return;

        // Не выключаем обход неожиданно: пользователь мог закрыть только окно,
        // рассчитывая продолжить пользоваться Discord/YouTube.
        e.Cancel = true;
        int choice = AppDialog.Show(this, "WhyNoDPI всё ещё работает",
            "Сетевой движок запущен. Что сделать перед закрытием приложения?",
            DialogTone.Warning,
            new[]
            {
                new DialogButton("Отмена", cancel: true),
                new DialogButton("Оставить в фоне"),
                new DialogButton("Остановить и закрыть", primary: true)
            });

        if (choice < 0 || choice == 0)
            return;

        if (choice == 1)
        {
            AddLog("Окно закрывается; сетевой движок оставлен работать в фоне.", LogLevel.Info);
            allowClose = true;
            Close();
            return;
        }

        isBusy = true;
        try
        {
            AddLog("Закрытие приложения: останавливаем сетевой движок…", LogLevel.Engine);
            await engineManager.StopAsync();
        }
        catch (Exception ex)
        {
            AddLog("Ошибка при остановке движка: " + ex.Message, LogLevel.Error);
        }
        finally
        {
            isBusy = false;
        }
        allowClose = true;
        Close();
    }
}
