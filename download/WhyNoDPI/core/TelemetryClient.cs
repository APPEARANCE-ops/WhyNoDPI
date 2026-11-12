using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NoDPI.Core;

/// <summary>
/// Anonymous telemetry for the admin panel: reports that this installation is
/// running so the administrator can see how many users are online.
///
/// Security and privacy rules:
///  - Sends ONLY an anonymous random GUID, app version, OS string and the
///    selected strategy file name. No personal data, no passwords, no proxy
///    secrets, no file paths, no IP addresses of the user.
///  - The server stores the GUID only as a salted SHA-256 hash (pepper lives
///    on the server), so telemetry cannot be linked back to a person.
///  - Fire-and-forget: never blocks the UI, never throws, silently ignores
///    network failures; works fully offline.
///  - Can be disabled in settings (TelemetryEnabled).
/// </summary>
public sealed class TelemetryClient : IDisposable
{
    /// <summary>
    /// Built-in default. The real address is configured in the app settings
    /// (Настройки → Сервер статистики) or with the WHYNODPI_TELEMETRY_URL
    /// environment variable, so the panel can move without a rebuild.
    /// Сборка «для пользователей» (Build-User.ps1 -PanelUrl ...) вшивает адрес
    /// через AssemblyMetadata, чтобы конечному пользователю ничего не настраивать.
    /// </summary>
    public static readonly string DefaultServerUrl = ResolveDefaultServerUrl();

    private static string ResolveDefaultServerUrl()
    {
        try
        {
            foreach (AssemblyMetadataAttribute attribute in
                     typeof(TelemetryClient).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (!string.Equals(attribute.Key, "DefaultPanelUrl", StringComparison.OrdinalIgnoreCase))
                    continue;
                string? value = attribute.Value?.Trim().TrimEnd('/');
                if (!string.IsNullOrWhiteSpace(value) && !IsPlaceholder(value))
                    return value;
            }
        }
        catch
        {
            // Metadata is optional: fall back to the placeholder below.
        }

        return "https://your-panel-domain.example";
    }

    private const int HeartbeatIntervalSeconds = 60;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(6);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    // UseProxy = false: иначе при «мёртвом» системном прокси (частая ситуация
    // после выключения VPN) heartbeat-и уходят в никуда и статистика молчит.
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    })
    {
        Timeout = RequestTimeout,
    };

    private readonly string folderPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhyNoDPI");
    private string StorePath => Path.Combine(folderPath, "telemetry.json");

    private Timer? timer;
    private string? installId;
    private string serverUrl = string.Empty;
    private string? currentStrategy;
    private int running;

    /// <summary>Panel address configured by the user (without a trailing slash).</summary>
    public string ServerUrl { get; set; } = string.Empty;

    /// <summary>Address actually used for heartbeats (settings → env → default).</summary>
    public string ActiveServerUrl => ResolveServerUrl();

    /// <summary>Latest selected strategy file; picked up by the next heartbeat.</summary>
    public string? CurrentStrategy
    {
        get => currentStrategy;
        set => currentStrategy = value;
    }

    /// <summary>
    /// True when a usable panel address exists. The placeholder domain means
    /// "not configured yet" and keeps telemetry switched off.
    /// </summary>
    public bool IsConfigured => !IsPlaceholder(ResolveServerUrl());

    public static bool IsPlaceholder(string? url) =>
        string.IsNullOrWhiteSpace(url) ||
        url.Contains("your-panel-domain", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Accepts "panel.vercel.app", "https://panel.vercel.app/" and returns
    /// "https://panel.vercel.app". Rejects anything that is not http(s).
    /// </summary>
    public static bool TryNormalizeUrl(string? input, out string normalized, out string error)
    {
        normalized = string.Empty;
        error = string.Empty;

        string value = (input ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            error = "Укажите адрес панели, например https://whynodpi.vercel.app";
            return false;
        }

        if (!value.Contains("://", StringComparison.Ordinal))
            value = "https://" + value.TrimStart('/');

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            error = "Не удалось разобрать адрес панели.";
            return false;
        }

        if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
        {
            error = "Адрес должен начинаться с https:// или http://.";
            return false;
        }

        string path = uri.AbsolutePath.TrimEnd('/');
        if (path.Length > 0)
        {
            error = "Укажите адрес панели без дополнительного пути, например https://whynodpi.vercel.app";
            return false;
        }

        normalized = $"{uri.Scheme}://{uri.Authority}";
        return true;
    }

    private string ResolveServerUrl()
    {
        string? env = Environment.GetEnvironmentVariable("WHYNODPI_TELEMETRY_URL");
        if (!string.IsNullOrWhiteSpace(env))
            return env.Trim().TrimEnd('/');

        string configured = (ServerUrl ?? string.Empty).Trim().TrimEnd('/');
        if (configured.Length > 0)
            return configured;

        return DefaultServerUrl.TrimEnd('/');
    }

    public void Start(string? strategyFile)
    {
        if (Interlocked.Exchange(ref running, 1) == 1)
            return; // already running

        serverUrl = ResolveServerUrl();
        if (IsPlaceholder(serverUrl))
        {
            // Panel URL is not configured yet: stay off instead of hammering a
            // placeholder address.
            Interlocked.Exchange(ref running, 0);
            return;
        }

        installId = LoadOrCreateInstallId();
        if (installId is null)
        {
            Interlocked.Exchange(ref running, 0);
            return;
        }

        currentStrategy = strategyFile;
        _ = SendAsync("start", strategyFile);

        timer = new Timer(
            _ => _ = SendAsync("heartbeat", currentStrategy),
            null,
            TimeSpan.FromSeconds(HeartbeatIntervalSeconds),
            TimeSpan.FromSeconds(HeartbeatIntervalSeconds));
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref running, 0) == 0)
            return;

        timer?.Dispose();
        timer = null;

        // Best-effort goodbye so the admin panel marks us offline immediately.
        _ = SendAsync("stop", null);
    }

    /// <summary>
    /// Checks that the panel answers. Used by the “Проверить связь” button so a
    /// typo in the address is visible before any heartbeat is sent.
    /// </summary>
    public static async Task<(bool Ok, string Message)> CheckAsync(
        string? url,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeUrl(url, out string normalized, out string error))
            return (false, error);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, normalized + "/api/health");
            request.Headers.UserAgent.ParseAdd("WhyNoDPI/" + GetAppVersion());
            request.Headers.Accept.ParseAdd("application/json");

            using HttpResponseMessage response = await Http
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            string body = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
                return (true, "Панель отвечает корректно.");

            if ((int)response.StatusCode == 503)
                return (false, "Панель отвечает, но база данных недоступна. Проверьте DATABASE_URL на Vercel.");

            return (false, $"Панель вернула HTTP {(int)response.StatusCode}. {Shorten(body)}");
        }
        catch (OperationCanceledException)
        {
            return (false, "Превышено время ожидания ответа панели.");
        }
        catch (Exception ex)
        {
            return (false, "Нет связи с панелью: " + ex.Message);
        }
    }

    private static string Shorten(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        string text = value.Trim().Replace('\n', ' ').Replace('\r', ' ');
        return text.Length <= 160 ? text : text[..160] + "…";
    }

    private string? LoadOrCreateInstallId()
    {
        try
        {
            Directory.CreateDirectory(folderPath);
            if (File.Exists(StorePath))
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(StorePath));
                    if (doc.RootElement.TryGetProperty("installId", out JsonElement id))
                    {
                        string? value = id.GetString();
                        if (!string.IsNullOrWhiteSpace(value) && value.Length <= 64)
                            return value;
                    }
                }
                catch (JsonException)
                {
                    // Corrupted store: regenerate below.
                }
            }

            string fresh = Guid.NewGuid().ToString("D");
            string json = JsonSerializer.Serialize(new TelemetryStore { InstallId = fresh }, JsonOptions);
            string tempPath = StorePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, StorePath, overwrite: true);
            return fresh;
        }
        catch
        {
            return null; // telemetry must never break the app
        }
    }

    private async Task SendAsync(string @event, string? strategyFile)
    {
        if (installId is null || serverUrl.Length == 0)
            return;

        try
        {
            var payload = new TelemetryPayload
            {
                InstallId = installId,
                Version = GetAppVersion(),
                Os = Environment.OSVersion.VersionString,
                Strategy = string.IsNullOrWhiteSpace(strategyFile) ? "-" : strategyFile,
                Event = @event,
            };

            using StringContent content = new(
                JsonSerializer.Serialize(payload, JsonOptions),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await Http
                .PostAsync(serverUrl + "/api/heartbeat", content)
                .ConfigureAwait(false);
            _ = response.StatusCode; // outcome is intentionally ignored
        }
        catch
        {
            // Offline, timeout, DNS failure — telemetry is never important
            // enough to surface an error to the user.
        }
    }

    private static string GetAppVersion()
    {
        try
        {
            Version? v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "unknown" : v.ToString(3);
        }
        catch
        {
            return "unknown";
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private sealed class TelemetryStore
    {
        public string InstallId { get; set; } = string.Empty;
    }

    private sealed class TelemetryPayload
    {
        public string InstallId { get; set; } = string.Empty;
        public string Version { get; set; } = "unknown";
        public string Os { get; set; } = "windows";
        public string Strategy { get; set; } = "-";
        public string Event { get; set; } = "heartbeat";
    }
}
