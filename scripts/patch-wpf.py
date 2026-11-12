from pathlib import Path
import sys

project = Path(sys.argv[1]).resolve().parent
def edit(path, old, new):
    p = project / path
    s = p.read_text(encoding="utf-8")
    if new in s:
        return
    if old not in s:
        raise SystemExit(f"Patch anchor missing in {path}: {old[:70]!r}")
    p.write_text(s.replace(old, new, 1), encoding="utf-8")

edit("core/SettingsManager.cs",
     "public bool TelemetryEnabled { get; set; } = true;",
     "public bool TelemetryEnabled { get; set; } = true;\n    public bool DeveloperModeEnabled { get; set; }")
edit("core/SettingsManager.cs",
     "TelemetryEnabled = saved.TelemetryEnabled;",
     "TelemetryEnabled = saved.TelemetryEnabled;\n            DeveloperModeEnabled = saved.DeveloperModeEnabled;")

(project / "core/ComplaintClient.cs").write_text(r'''using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace NoDPI.Core;

public sealed class ComplaintClient
{
    public const string DefaultServerUrl = "https://why-no-dpiw.vercel.app";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public async Task SubmitAsync(string category, string message, string? diagnostics, string strategy)
    {
        string baseUrl = Environment.GetEnvironmentVariable("WHYNODPI_REPORT_URL")?.TrimEnd('/') ?? DefaultServerUrl;
        var payload = new {
            category, message, diagnostics,
            version = typeof(ComplaintClient).Assembly.GetName().Version?.ToString(3) ?? "unknown",
            os = Environment.OSVersion.VersionString,
            strategy = strategy
        };
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await Http.PostAsync(baseUrl + "/api/reports", content).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) {
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"Сервер отклонил обращение (HTTP {(int)response.StatusCode}): {body}");
        }
    }
}
''', encoding="utf-8")

edit("MainWindow.xaml",
     '<Button Content="▤   Журнал" Style="{StaticResource NavButton}" Click="Journal_Click"/>',
     '<Button Content="▤   Журнал" Style="{StaticResource NavButton}" Click="Journal_Click"/>\n                    <Button x:Name="DeveloperNavButton" Content="⌘   Разработчик" Style="{StaticResource NavButton}" Click="Developer_Click" Visibility="Collapsed"/>\n                    <Button Content="⚑   Сообщить о проблеме" Style="{StaticResource NavButton}" Click="Report_Click"/>')

edit("MainWindow.xaml",
'''                        <Border Style="{StaticResource CardBorder}">
                            <StackPanel>
                                <TextBlock Text="О проекте"''',
'''                        <Border Style="{StaticResource CardBorder}" Margin="0,0,0,14">
                            <StackPanel>
                                <TextBlock Text="Режим разработчика" FontSize="17" FontWeight="Bold"/>
                                <CheckBox x:Name="DeveloperModeBox" Content="Включить расширенный режим разработчика" Checked="DeveloperModeChanged" Unchecked="DeveloperModeChanged" Margin="0,10,0,0"/>
                                <TextBlock Text="Открывает расширенную диагностику и сведения о WinDivert. Не изменяет настройки системы автоматически." Foreground="{StaticResource MutedBrush}" FontSize="12" TextWrapping="Wrap" Margin="0,6,0,0"/>
                            </StackPanel>
                        </Border>
                        <Border Style="{StaticResource CardBorder}">
                            <StackPanel>
                                <TextBlock Text="О проекте"''')

edit("MainWindow.xaml",
'''            </Grid>
        </Grid>
    </Grid>
</Window>''',
'''            </Grid>
            <Grid x:Name="DeveloperPage" Visibility="Collapsed">
                <ScrollViewer VerticalScrollBarVisibility="Auto">
                    <StackPanel Margin="3,2,5,18">
                        <TextBlock Text="Режим разработчика" FontSize="29" FontWeight="Bold"/>
                        <TextBlock Text="Диагностика окружения, WinDivert, сети и журналов" Foreground="{StaticResource MutedBrush}" FontSize="13" Margin="0,4,0,16"/>
                        <Border Style="{StaticResource CardBorder}">
                            <StackPanel>
                                <TextBlock Text="Диагностика системы" FontSize="17" FontWeight="Bold"/>
                                <TextBlock Text="Проверки не меняют настройки Windows или файлы пакета." Foreground="{StaticResource MutedBrush}" FontSize="12" TextWrapping="Wrap" Margin="0,5,0,12"/>
                                <WrapPanel>
                                    <Button Content="Запустить полную диагностику" Click="DeveloperDiagnostics_Click"/>
                                    <Button Content="Открыть папку логов" Style="{StaticResource SecondaryButton}" Click="OpenLogs_Click" Margin="8,0,0,0"/>
                                    <Button Content="Скопировать отчёт" Style="{StaticResource SecondaryButton}" Click="CopyDiagnostics_Click" Margin="8,0,0,0"/>
                                </WrapPanel>
                                <TextBox x:Name="DeveloperDiagnosticsText" Margin="0,14,0,0" MinHeight="240" IsReadOnly="True" TextWrapping="Wrap" AcceptsReturn="True" VerticalScrollBarVisibility="Auto" Background="#0B1423" Foreground="#C6D4E9" BorderBrush="{StaticResource OutlineBrush}" Padding="12"/>
                            </StackPanel>
                        </Border>
                    </StackPanel>
                </ScrollViewer>
            </Grid>
            <Grid x:Name="ReportPage" Visibility="Collapsed">
                <ScrollViewer VerticalScrollBarVisibility="Auto">
                    <StackPanel Margin="3,2,5,18">
                        <TextBlock Text="Сообщить о проблеме" FontSize="29" FontWeight="Bold"/>
                        <TextBlock Text="Обращение попадёт в админ-панель. Не указывай пароли, ключи и личные данные." Foreground="{StaticResource MutedBrush}" FontSize="13" TextWrapping="Wrap" Margin="0,4,0,16"/>
                        <Border Style="{StaticResource CardBorder}">
                            <StackPanel>
                                <TextBlock Text="Категория" FontWeight="SemiBold"/>
                                <ComboBox x:Name="ReportCategoryBox" SelectedIndex="0" Margin="0,6,0,14">
                                    <ComboBoxItem Content="Ошибка запуска / WinDivert"/>
                                    <ComboBoxItem Content="Проблема со стратегией"/>
                                    <ComboBoxItem Content="Сеть / доступ к сервисам"/>
                                    <ComboBoxItem Content="Интерфейс / другое"/>
                                </ComboBox>
                                <TextBlock Text="Описание" FontWeight="SemiBold"/>
                                <TextBox x:Name="ReportMessageBox" MinHeight="130" AcceptsReturn="True" TextWrapping="Wrap" VerticalScrollBarVisibility="Auto" Margin="0,6,0,12" Padding="10"/>
                                <CheckBox x:Name="AttachDiagnosticsBox" Content="Приложить диагностический отчёт и последние 150 строк журнала (необязательно)" Margin="0,0,0,14"/>
                                <Button x:Name="SendReportButton" Content="Отправить обращение" Click="SendReport_Click" HorizontalAlignment="Left"/>
                                <TextBlock x:Name="ReportStatusText" Foreground="{StaticResource MutedBrush}" TextWrapping="Wrap" Margin="0,10,0,0"/>
                            </StackPanel>
                        </Border>
                    </StackPanel>
                </ScrollViewer>
            </Grid>
        </Grid>
    </Grid>
</Window>''')

edit("MainWindow.xaml.cs",
     "using System.Linq;",
     "using System.Linq;\nusing System.Text;\nusing System.Security.Principal;")
edit("MainWindow.xaml.cs",
     "private readonly TelemetryClient telemetry = new();",
     "private readonly TelemetryClient telemetry = new();\n    private readonly ComplaintClient complaintClient = new();\n    private string? latestDiagnostics;")
edit("MainWindow.xaml.cs",
     "TelemetryBox.IsChecked = settings.TelemetryEnabled;",
     "TelemetryBox.IsChecked = settings.TelemetryEnabled;\n        DeveloperModeBox.IsChecked = settings.DeveloperModeEnabled;\n        DeveloperNavButton.Visibility = settings.DeveloperModeEnabled ? Visibility.Visible : Visibility.Collapsed;")
edit("MainWindow.xaml.cs",
'''        SettingsPage.Visibility = Visibility.Collapsed;
        page.Visibility = Visibility.Visible;''',
'''        SettingsPage.Visibility = Visibility.Collapsed;
        DeveloperPage.Visibility = Visibility.Collapsed;
        ReportPage.Visibility = Visibility.Collapsed;
        page.Visibility = Visibility.Visible;''')

methods = r'''
    private void Developer_Click(object sender, RoutedEventArgs e)
    {
        if (settings.DeveloperModeEnabled) ShowPage(DeveloperPage, "Режим разработчика");
    }
    private void Report_Click(object sender, RoutedEventArgs e) => ShowPage(ReportPage, "Сообщить о проблеме");

    private void DeveloperModeChanged(object sender, RoutedEventArgs e)
    {
        if (isLoadingSettings) return;
        settings.DeveloperModeEnabled = DeveloperModeBox.IsChecked == true;
        DeveloperNavButton.Visibility = settings.DeveloperModeEnabled ? Visibility.Visible : Visibility.Collapsed;
        SaveSettings();
        AddLog(settings.DeveloperModeEnabled ? "Включён режим разработчика." : "Режим разработчика выключен.");
        if (!settings.DeveloperModeEnabled && DeveloperPage.Visibility == Visibility.Visible)
            ShowPage(SettingsPage, "Настройки");
    }

    private async void DeveloperDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        DeveloperDiagnosticsText.Text = "Выполняется диагностика…";
        try
        {
            var b = new StringBuilder();
            b.AppendLine("WhyNoDPI — диагностический отчёт");
            b.AppendLine("Время: " + DateTimeOffset.Now.ToString("O"));
            b.AppendLine("Версия: " + (GetType().Assembly.GetName().Version?.ToString() ?? "unknown"));
            b.AppendLine("ОС: " + Environment.OSVersion);
            b.AppendLine("Архитектура ОС x64: " + Environment.Is64BitOperatingSystem);
            b.AppendLine("Администратор: " + IsAdministrator());
            b.AppendLine("Папка Flowseal: " + (string.IsNullOrWhiteSpace(settings.ZapretDirectory) ? "не выбрана" : settings.ZapretDirectory));
            b.AppendLine("Профиль: " + settings.ZapretStrategyFile);
            b.AppendLine("winws.exe WhyNoDPI активен: " + engineManager.RefreshStatus());
            b.AppendLine("Служба Zapret: " + engineManager.IsZapretServiceRunning);
            b.AppendLine("Любой winws.exe: " + engineManager.IsAnyWinwsProcessRunning);
            b.AppendLine("Последний лог движка: " + (engineManager.LastLogPath ?? "нет"));
            b.AppendLine("\nПроверка пакета:");
            if (!string.IsNullOrWhiteSpace(settings.ZapretDirectory) && Directory.Exists(settings.ZapretDirectory))
                b.AppendLine(EngineManager.BuildDiagnostics(settings.ZapretDirectory, settings.ZapretStrategyFile));
            else b.AppendLine("Папка Flowseal не выбрана.");
            b.AppendLine("\nСетевые проверки:");
            foreach (var check in await diagnostics.CheckImportantServicesAsync())
                b.AppendLine($"{check.Service}: {check.Details}");
            b.AppendLine("\nWinDivert service query:");
            b.AppendLine(await QueryWinDivertAsync());
            latestDiagnostics = b.ToString();
            DeveloperDiagnosticsText.Text = latestDiagnostics;
            AddLog("Полная диагностика завершена.");
        }
        catch (Exception ex)
        {
            latestDiagnostics = "Ошибка диагностики: " + ex;
            DeveloperDiagnosticsText.Text = latestDiagnostics;
            AddLog("Ошибка диагностики разработчика: " + ex.Message);
        }
    }

    private static bool IsAdministrator()
    {
        try { using var identity = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); }
        catch { return false; }
    }

    private static async Task<string> QueryWinDivertAsync()
    {
        try
        {
            using var p = new Process { StartInfo = new ProcessStartInfo("sc.exe", "query WinDivert") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
            p.Start();
            string output = await p.StandardOutput.ReadToEndAsync();
            string error = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            return string.IsNullOrWhiteSpace(output) ? error : output.Trim();
        }
        catch (Exception ex) { return "Не удалось запросить службу: " + ex.Message; }
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(latestDiagnostics ?? DeveloperDiagnosticsText.Text); AddLog("Диагностический отчёт скопирован."); }
        catch (Exception ex) { AddLog("Не удалось скопировать отчёт: " + ex.Message); }
    }

    private async void SendReport_Click(object sender, RoutedEventArgs e)
    {
        string message = ReportMessageBox.Text.Trim();
        if (message.Length < 10 || message.Length > 4000)
        {
            ReportStatusText.Text = "Описание должно содержать от 10 до 4000 символов.";
            return;
        }
        string category = (ReportCategoryBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Другое";
        string? attached = null;
        if (AttachDiagnosticsBox.IsChecked == true)
        {
            if (string.IsNullOrWhiteSpace(latestDiagnostics))
            {
                ReportStatusText.Text = "Сначала запусти диагностику в режиме разработчика либо отключи приложение отчёта.";
                return;
            }
            attached = latestDiagnostics + "\nПоследние события журнала:\n" +
                string.Join("\n", LogList.Items.Cast<object>().Take(150).Select(x => x.ToString()));
            if (attached.Length > 18000) attached = attached[..18000];
        }
        SendReportButton.IsEnabled = false;
        ReportStatusText.Text = "Отправляем обращение…";
        try
        {
            await complaintClient.SubmitAsync(category, message, attached, settings.ZapretStrategyFile);
            ReportStatusText.Text = "Обращение принято сервером и появится в админ-панели.";
            ReportMessageBox.Clear();
            AddLog("Обращение отправлено на сервер поддержки.");
        }
        catch (Exception ex)
        {
            ReportStatusText.Text = "Не удалось отправить обращение: " + ex.Message;
            AddLog("Ошибка отправки обращения: " + ex.Message);
        }
        finally { SendReportButton.IsEnabled = true; }
    }

'''
edit("MainWindow.xaml.cs",
     "    private void Home_Click(object sender, RoutedEventArgs e)",
     methods + "    private void Home_Click(object sender, RoutedEventArgs e)")
edit("MainWindow.xaml.cs",
'''        LogList.Items.Insert(0, $"[{DateTime.Now:HH:mm:ss}]  {message}");
        while (LogList.Items.Count > 500)''',
'''        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]  {message}";
        LogList.Items.Insert(0, line);
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhyNoDPI", "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "whynodpi.log"), line + Environment.NewLine, Encoding.UTF8);
        }
        catch { }
        while (LogList.Items.Count > 500)''')

print("WPF support/report and developer-mode patch applied.")
