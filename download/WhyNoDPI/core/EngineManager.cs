using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NoDPI.Core;

/// <summary>Immutable view of the network engine as seen by the UI.</summary>
public readonly record struct EngineSnapshot(
    bool OwnedRunning,
    bool ServiceRunning,
    bool AnyWinwsProcess,
    int? ProcessId)
{
    public bool AnyProtected => OwnedRunning || ServiceRunning || AnyWinwsProcess;
}

/// <summary>
/// Starts and stops winws.exe directly. A selected Flowseal General file is
/// parsed as a parameter template only; cmd.exe and BAT files are never run.
/// </summary>
public sealed class EngineManager
{
    private string? _engineExecutable;

    public bool IsRunning => HasOwnedProcesses();
    public bool IsAnyWinwsProcessRunning => HasAnyWinwsProcess();
    public bool IsZapretServiceRunning => QueryServiceRunning("zapret");
    public string? ActiveStrategyFile { get; private set; }
    public int? ActiveProcessId { get; private set; }
    public string? LastLogPath { get; private set; }
    public string? LastFailureMessage { get; private set; }

    /// <summary>
    /// One process scan instead of three. The UI polls this every few seconds,
    /// and enumerating winws.exe three times per tick was wasteful.
    /// </summary>
    public EngineSnapshot GetSnapshot(bool queryService = true)
    {
        List<Process> owned = FindOwnedProcesses();
        bool ownedRunning = owned.Count > 0;
        int? pid = null;
        if (ownedRunning)
        {
            try { pid = owned[0].Id; } catch { }
        }
        foreach (Process process in owned)
            process.Dispose();

        bool anyWinws = ownedRunning || HasAnyWinwsProcess();
        bool serviceRunning = !ownedRunning && queryService && QueryServiceRunning("zapret");
        return new EngineSnapshot(ownedRunning, serviceRunning, anyWinws, pid);
    }

    public event Action<string>? StatusChanged;

    public void Configure(string bundleDirectory)
    {
        if (string.IsNullOrWhiteSpace(bundleDirectory))
        {
            _engineExecutable = null;
            return;
        }

        try
        {
            string root = ResolveBundleRoot(bundleDirectory);
            _engineExecutable = Path.Combine(root, "bin", "winws.exe");
        }
        catch
        {
            // Keep startup resilient when a previously saved folder was moved.
            _engineExecutable = Path.Combine(Path.GetFullPath(bundleDirectory), "bin", "winws.exe");
        }
    }

    /// <summary>
    /// Accepts either the Flowseal package root or its parent directory.
    /// Only the package itself or one immediate child directory is searched.
    /// </summary>
    public static string ResolveBundleRoot(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("Сначала выберите папку Flowseal в настройках.");

        string root = Path.GetFullPath(directory);
        if (LooksLikeBundle(root))
            return Path.TrimEndingDirectorySeparator(root);

        if (Directory.Exists(root))
        {
            foreach (string child in Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly))
            {
                if (LooksLikeBundle(child))
                    return Path.TrimEndingDirectorySeparator(Path.GetFullPath(child));
            }
        }

        throw new DirectoryNotFoundException(
            "Не удалось найти пакет Flowseal. Выберите папку, где находятся bin\\winws.exe, папка lists и кэш профилей WhyNoDPI, созданный при первом выборе пакета.");
    }

    private static bool LooksLikeBundle(string root)
    {
        if (!Directory.Exists(root))
            return false;

        bool hasEngine = File.Exists(Path.Combine(root, "bin", "winws.exe"));
        bool hasLists = Directory.Exists(Path.Combine(root, "lists"));
        bool hasProfile = FlowsealProfileCatalog.HasCache(root) ||
                          Directory.GetFiles(root, "general*.bat", SearchOption.TopDirectoryOnly).Length > 0;
        return hasEngine && hasLists && hasProfile;
    }

    public async Task<bool> StartAsync(string bundleDirectory, string strategyFile)
    {
        LastLogPath = null;
        LastFailureMessage = null;
        if (!IsAdministrator())
        {
            LastFailureMessage = "Для WinDivert нужны права администратора. Закройте WhyNoDPI и запустите его от имени администратора.";
            StatusChanged?.Invoke(LastFailureMessage);
            return false;
        }

        string root = ResolveBundleRoot(bundleDirectory);
        Configure(root);

        string profileName = Path.GetFileName(strategyFile ?? string.Empty);
        if (string.IsNullOrWhiteSpace(profileName))
            throw new InvalidOperationException("Выберите профиль General перед запуском.");

        string profilePath = Path.GetFullPath(Path.Combine(root, profileName));
        string winws = Path.Combine(root, "bin", "winws.exe");
        ValidateBundle(root, profilePath, winws);

        if (IsZapretServiceRunning)
        {
            LastFailureMessage = "Служба Windows «zapret» уже запущена. Остановите её через средство управления Flowseal, затем запустите профиль из WhyNoDPI. Второй движок поверх службы запускаться не будет.";
            StatusChanged?.Invoke(LastFailureMessage);
            return false;
        }

        if (IsAnyWinwsProcessRunning)
        {
            if (IsRunning)
            {
                ActiveStrategyFile = profileName;
                StatusChanged?.Invoke("Движок WhyNoDPI уже работает.");
                return true;
            }

            LastFailureMessage = "Обнаружен внешний winws.exe. Сначала остановите профиль, запущенный вне WhyNoDPI, чтобы не создавать два сетевых движка.";
            StatusChanged?.Invoke(LastFailureMessage);
            return false;
        }

        StatusChanged?.Invoke($"Подготовка профиля {Path.GetFileNameWithoutExtension(profilePath)}...");

        string binDirectory = Path.Combine(root, "bin");
        string listsDirectory = Path.Combine(root, "lists");
        EnsureFlowsealUserLists(listsDirectory);
        string profileTemplate = FlowsealProfileCatalog.GetCommandTemplate(root, profileName);
        string[] arguments = ParseFlowsealProfile(profileTemplate, root);
        if (arguments.Length == 0)
            throw new InvalidOperationException("В выбранном профиле не найдены параметры запуска winws.exe.");

        string logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WhyNoDPI", "logs");
        Directory.CreateDirectory(logDirectory);
        string logPath = Path.Combine(logDirectory, $"start-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
        LastLogPath = logPath;

        var logLines = new List<string>
        {
            "WhyNoDPI native winws launch log",
            $"Time: {DateTime.Now:O}",
            $"Bundle: {root}",
            $"Profile definition: {profilePath}",
            $"Executable: {winws}",
            $"Working directory: {binDirectory}",
            $"Administrator: {IsAdministrator()}",
            $"Zapret service running: {IsZapretServiceRunning}",
            "Launch mode: direct Process.Start(winws.exe); no cmd.exe and no BAT execution",
            new string('-', 72),
            "Arguments:"
        };
        logLines.AddRange(arguments.Select((arg, index) => $"[{index}] {arg}"));
        await File.WriteAllTextAsync(logPath, string.Join(Environment.NewLine, logLines) + Environment.NewLine,
            new UTF8Encoding(false));

        try
        {
            // Flowseal's helper script enables TCP timestamps before starting winws.
            // Apply the same setting directly without invoking service.bat.
            await EnableTcpTimestampsAsync();

            var startInfo = new ProcessStartInfo
            {
                FileName = winws,
                WorkingDirectory = binDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var launcher = new Process { StartInfo = startInfo };
            if (!launcher.Start())
                throw new InvalidOperationException("Windows не смогла запустить bin\\winws.exe.");

            // winws is a long-lived process. Confirm it starts and remains alive
            // instead of assuming that Process.Start alone means success.
            for (int attempt = 0; attempt < 16; attempt++)
            {
                await Task.Delay(250);
                if (launcher.HasExited)
                {
                    int exitCode = launcher.ExitCode;
                    ActiveStrategyFile = null;
                    LastFailureMessage = $"winws.exe завершился сразу после запуска (код {exitCode}). Проверьте журнал: {logPath}";
                    await File.AppendAllTextAsync(logPath,
                        $"{Environment.NewLine}winws exited immediately. Exit code: {exitCode}{Environment.NewLine}",
                        new UTF8Encoding(false));
                    StatusChanged?.Invoke(LastFailureMessage);
                    return false;
                }

                if (HasOwnedProcesses())
                {
                    ActiveStrategyFile = profileName;
                    ActiveProcessId = launcher.Id;
                    LastFailureMessage = null;
                    await File.AppendAllTextAsync(logPath,
                        $"{Environment.NewLine}Started successfully. PID: {launcher.Id}{Environment.NewLine}",
                        new UTF8Encoding(false));
                    StatusChanged?.Invoke($"Профиль {Path.GetFileNameWithoutExtension(profilePath)} запущен напрямую через winws.exe. Журнал: {logPath}");
                    return true;
                }
            }

            try
            {
                if (!launcher.HasExited)
                    launcher.Kill(entireProcessTree: true);
            }
            catch { }

            ActiveStrategyFile = null;
            LastFailureMessage = "Процесс winws.exe не появился в списке процессов после запуска. Проверьте WinDivert, права администратора и журнал: " + logPath;
            StatusChanged?.Invoke(LastFailureMessage);
            return false;
        }
        catch (Exception ex)
        {
            ActiveStrategyFile = null;
            LastFailureMessage = "Не удалось запустить сетевой движок напрямую: " + ex.Message + $" Журнал: {logPath}";
            await File.AppendAllTextAsync(logPath,
                $"{Environment.NewLine}EXCEPTION: {ex}{Environment.NewLine}", new UTF8Encoding(false));
            StatusChanged?.Invoke(LastFailureMessage);
            return false;
        }
    }

    /// <summary>
    /// Expands a cached Flowseal argument template and returns argv for winws.exe.
    /// General files are imported once into the WhyNoDPI JSON catalog. They are
    /// not needed after import and are never executed.
    /// </summary>
    private static string[] ParseFlowsealProfile(string commandTemplate, string root)
    {
        string bin = Path.GetFullPath(Path.Combine(root, "bin")) + Path.DirectorySeparatorChar;
        string lists = Path.GetFullPath(Path.Combine(root, "lists")) + Path.DirectorySeparatorChar;
        (string gameTcp, string gameUdp) = ReadGameFilter(root);

        string expanded = commandTemplate
            .Replace("%BIN%", bin, StringComparison.OrdinalIgnoreCase)
            .Replace("%LISTS%", lists, StringComparison.OrdinalIgnoreCase)
            .Replace("%~dp0", Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            .Replace("%GameFilterTCP%", gameTcp, StringComparison.OrdinalIgnoreCase)
            .Replace("%GameFilterUDP%", gameUdp, StringComparison.OrdinalIgnoreCase);

        Match unresolved = Regex.Match(expanded, @"%[^%\r\n]+%");
        if (unresolved.Success)
            throw new InvalidOperationException($"Не удалось подставить переменную профиля {unresolved.Value}. Профиль Flowseal мог изменить формат.");

        return SplitCommandLineArguments(expanded).ToArray();
    }

    private static List<string> SplitCommandLineArguments(string commandLine)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < commandLine.Length; i++)
        {
            char ch = commandLine[i];
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }
            current.Append(ch);
        }

        if (inQuotes)
            throw new InvalidDataException("В параметрах выбранного General обнаружены незакрытые кавычки.");
        if (current.Length > 0)
            result.Add(current.ToString());
        return result;
    }

    private static (string Tcp, string Udp) ReadGameFilter(string root)
    {
        string flagFile = Path.Combine(root, "utils", "game_filter.enabled");
        const string defaultRange = "1024-65535";
        if (!File.Exists(flagFile))
            return ("12", "12");

        string mode = "disabled";
        string tcpRange = defaultRange;
        string udpRange = defaultRange;
        string? tcpCandidate = null;
        string? udpCandidate = null;

        foreach (string line in File.ReadLines(flagFile))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;

            int separator = trimmed.IndexOf('=');
            string key = separator >= 0 ? trimmed[..separator].Trim() : trimmed;
            string value = separator >= 0 ? trimmed[(separator + 1)..].Trim() : string.Empty;

            if (key.Equals("mode", StringComparison.OrdinalIgnoreCase))
                mode = value;
            else if (key.Equals("all", StringComparison.OrdinalIgnoreCase))
                mode = "all";
            else if (key.Equals("tcp", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Length == 0) mode = "tcp";
                else tcpCandidate = value;
            }
            else if (key.Equals("udp", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Length == 0) mode = "udp";
                else udpCandidate = value;
            }
        }

        if (TryNormalizePortRanges(tcpCandidate, out string parsedTcp))
            tcpRange = parsedTcp;
        if (TryNormalizePortRanges(udpCandidate, out string parsedUdp))
            udpRange = parsedUdp;

        if (mode.Equals("all", StringComparison.OrdinalIgnoreCase))
            return (tcpRange, udpRange);
        if (mode.Equals("tcp", StringComparison.OrdinalIgnoreCase))
            return (tcpRange, "12");
        if (mode.Equals("udp", StringComparison.OrdinalIgnoreCase))
            return ("12", udpRange);

        return ("12", "12");
    }

    private static bool TryNormalizePortRanges(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var parts = new List<string>();
        foreach (string rawPart in value.Replace(" ", string.Empty, StringComparison.Ordinal).Split(','))
        {
            if (rawPart.Length == 0)
                return false;

            string[] bounds = rawPart.Split('-');
            if (bounds.Length is < 1 or > 2 ||
                !int.TryParse(bounds[0], out int first) ||
                first is < 1 or > 65535)
                return false;

            int last = first;
            if (bounds.Length == 2 &&
                (!int.TryParse(bounds[1], out last) || last is < 1 or > 65535 || first > last))
                return false;

            parts.Add(bounds.Length == 1 ? first.ToString() : $"{first}-{last}");
        }

        normalized = string.Join(',', parts);
        return true;
    }

    private static void EnsureFlowsealUserLists(string listsDirectory)
    {
        Directory.CreateDirectory(listsDirectory);
        string ipsetExclude = Path.Combine(listsDirectory, "ipset-exclude-user.txt");
        string generalUser = Path.Combine(listsDirectory, "list-general-user.txt");
        string excludeUser = Path.Combine(listsDirectory, "list-exclude-user.txt");

        if (!File.Exists(ipsetExclude))
            File.WriteAllText(ipsetExclude, "203.0.113.113/32" + Environment.NewLine, new UTF8Encoding(false));
        if (!File.Exists(generalUser))
            File.WriteAllText(generalUser, "# Never leave this file empty" + Environment.NewLine + "domain.example.abc" + Environment.NewLine, new UTF8Encoding(false));
        if (!File.Exists(excludeUser))
            File.WriteAllText(excludeUser, "domain.example.abc" + Environment.NewLine, new UTF8Encoding(false));
    }

    private static async Task EnableTcpTimestampsAsync()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "netsh.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("interface");
        startInfo.ArgumentList.Add("tcp");
        startInfo.ArgumentList.Add("set");
        startInfo.ArgumentList.Add("global");
        startInfo.ArgumentList.Add("timestamps=enabled");

        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is not null)
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // TCP timestamp tuning is a best-effort compatibility step. It must
            // never prevent the native winws process from being launched.
        }
    }

    public async Task StopAsync()
    {
        List<Process> processes = FindOwnedProcesses();
        if (processes.Count == 0)
        {
            ActiveStrategyFile = null;
            ActiveProcessId = null;
            StatusChanged?.Invoke("Процесс winws.exe, запущенный из WhyNoDPI, не найден.");
            return;
        }

        StatusChanged?.Invoke("Остановка сетевого движка...");
        foreach (Process process in processes)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(6));
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"Не удалось завершить winws.exe (PID {SafeId(process)}): {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }

        ActiveStrategyFile = null;
        ActiveProcessId = null;
        StatusChanged?.Invoke(HasOwnedProcesses()
            ? "Не все процессы движка удалось остановить."
            : "Сетевой движок остановлен.");
    }

    public bool RefreshStatus() => HasOwnedProcesses();

    public static void ValidateBundle(string root, string script, string winws)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        script = Path.GetFullPath(script);
        winws = Path.GetFullPath(winws);

        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Папка Flowseal не найдена: {root}");

        if (!Directory.Exists(Path.Combine(root, "lists")))
            throw new DirectoryNotFoundException("Не найдена папка lists. Скачайте и распакуйте полный пакет Flowseal.");

        if (!File.Exists(winws))
            throw new FileNotFoundException("Не найден bin\\winws.exe. Скачайте полный архив Flowseal.", winws);

        string profileName = Path.GetFileName(script);
        if (!profileName.StartsWith("general", StringComparison.OrdinalIgnoreCase) ||
            !profileName.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Можно выбрать только профиль General из пакета Flowseal.");

        _ = FlowsealProfileCatalog.GetCommandTemplate(root, profileName);

        string binDirectory = Path.Combine(root, "bin");
        bool hasDriver = Directory.Exists(binDirectory) &&
                         Directory.GetFiles(binDirectory, "*.sys", SearchOption.TopDirectoryOnly).Length > 0;
        if (!hasDriver)
            throw new FileNotFoundException("В папке bin не найден драйвер *.sys (WinDivert). Проверьте, что распакован полный архив Flowseal.");
    }

    private bool HasOwnedProcesses()
    {
        List<Process> processes = FindOwnedProcesses();
        bool found = processes.Count > 0;
        foreach (Process process in processes)
            process.Dispose();
        return found;
    }

    private bool HasAnyWinwsProcess()
    {
        Process[] processes;
        try { processes = Process.GetProcessesByName("winws"); }
        catch { return false; }

        bool found = processes.Length > 0;
        foreach (Process process in processes)
            process.Dispose();
        return found;
    }

    private static bool QueryServiceRunning(string serviceName)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "sc.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("query");
            startInfo.ArgumentList.Add(serviceName);

            using Process? process = Process.Start(startInfo);
            if (process is null)
                return false;

            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(2500))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            string output = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
            return Regex.IsMatch(output, @"STATE\s*:\s*4\s+RUNNING", RegexOptions.IgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static string BuildDiagnostics(string bundleDirectory, string strategyFile)
    {
        Process[] runningProcesses;
        try { runningProcesses = Process.GetProcessesByName("winws"); }
        catch { runningProcesses = Array.Empty<Process>(); }
        bool hasWinws = runningProcesses.Length > 0;
        foreach (Process process in runningProcesses)
            process.Dispose();

        var lines = new List<string>
        {
            "WhyNoDPI diagnostics",
            $"Time: {DateTime.Now:O}",
            $"Administrator: {IsAdministrator()}",
            $"Windows: {Environment.OSVersion}",
            $"64-bit OS: {Environment.Is64BitOperatingSystem}",
            $"zapret service running: {QueryServiceRunning("zapret")}",
            $"winws.exe process running: {hasWinws}"
        };

        if (string.IsNullOrWhiteSpace(bundleDirectory))
        {
            lines.Add("Flowseal folder: not selected");
            return string.Join(Environment.NewLine, lines);
        }

        try
        {
            string root = ResolveBundleRoot(bundleDirectory);
            string script = Path.Combine(root, Path.GetFileName(strategyFile ?? "general.bat"));
            string winws = Path.Combine(root, "bin", "winws.exe");
            ValidateBundle(root, script, winws);
            lines.Add($"Flowseal folder: {root}");
            lines.Add($"Selected profile: {script}");
            lines.Add($"winws.exe: {File.Exists(winws)}");
            string binDirectory = Path.Combine(root, "bin");
            lines.Add($"WinDivert driver: {Directory.Exists(binDirectory) && Directory.GetFiles(binDirectory, "*.sys", SearchOption.TopDirectoryOnly).Length > 0}");
            if (root.Any(ch => ch > 127) || root.Contains(' '))
                lines.Add("WARNING: folder path contains spaces or non-ASCII characters; Flowseal recommends a simple path such as C:\\zapret-flowseal.");
            lines.Add("Result: package files look valid.");
        }
        catch (Exception ex)
        {
            lines.Add("Flowseal folder validation: FAILED");
            lines.Add(ex.Message);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private List<Process> FindOwnedProcesses()
    {
        var matches = new List<Process>();
        if (string.IsNullOrWhiteSpace(_engineExecutable) || !File.Exists(_engineExecutable))
            return matches;

        string expected = NormalizePath(_engineExecutable);
        foreach (Process process in Process.GetProcessesByName("winws"))
        {
            try
            {
                string? path = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(path) &&
                    string.Equals(NormalizePath(path), expected, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(process);
                    continue;
                }
            }
            catch
            {
                // Do not terminate processes whose executable path cannot be verified.
            }
            process.Dispose();
        }
        return matches;
    }

    private static string NormalizePath(string path)
    {
        string full = Path.GetFullPath(path);
        if (full.StartsWith("\\\\?\\", StringComparison.Ordinal))
            full = full[4..];
        return Path.TrimEndingDirectorySeparator(full);
    }

    private static bool IsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static int SafeId(Process process)
    {
        try { return process.Id; } catch { return -1; }
    }
}
