using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NoDPI.Core;

/// <summary>Сведения о новой версии самого приложения.</summary>
public sealed record AppUpdateInfo(string LatestVersion, string? DownloadUrl, string? Notes, bool IsNewer);

/// <summary>Сведения о новой версии пакета Flowseal (zapret).</summary>
public sealed record ZapretUpdateInfo(
    string LatestTag,
    DateTime PublishedUtc,
    DateTime LocalUtc,
    string? ReleaseUrl,
    bool IsNewer);

/// <summary>
/// Проверка обновлений:
///  • WhyNoDPI — через /api/version своей же панели (она знает актуальную версию);
///  • пакет Flowseal — через публичный GitHub Releases.
/// Обе проверки необязательные: любая ошибка сети просто означает «не знаю».
/// </summary>
public static class UpdateChecker
{
    private const string GitHubLatestRelease =
        "https://api.github.com/repos/Flowseal/zapret-discord-youtube/releases/latest";

    // UseProxy = false: системный прокси в Windows бывает выключен/мёртвым
    // (например, 127.0.0.1:8080 от остановленного VPN), и тогда любая проверка
    // молча ломается. Обновления качаются напрямую.
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(6),
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    })
    {
        Timeout = TimeSpan.FromSeconds(12),
    };

    public static async Task<AppUpdateInfo?> CheckAppAsync(
        string? panelUrl,
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(panelUrl) ||
            panelUrl.Contains("your-panel-domain", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            string endpoint = panelUrl.TrimEnd('/') + "/api/version";
            using HttpResponseMessage response = await Http
                .GetAsync(endpoint, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            await using Stream stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using JsonDocument document = await JsonDocument
                .ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!TryReadString(document.RootElement, "version", out string latest) || latest.Length == 0)
                return null;

            TryReadString(document.RootElement, "url", out string downloadUrl);
            TryReadString(document.RootElement, "notes", out string notes);

            bool isNewer = TryParseVersion(latest, out Version? latestVersion) &&
                           TryParseVersion(currentVersion, out Version? current) &&
                           latestVersion > current;

            return new AppUpdateInfo(
                latest,
                downloadUrl.Length == 0 ? null : downloadUrl,
                notes.Length == 0 ? null : notes,
                isNewer);
        }
        catch
        {
            // Нет сети, панель недоступна, кривой ответ — молча считаем, что
            // проверить не удалось.
            return null;
        }
    }

    public static async Task<ZapretUpdateInfo?> CheckZapretAsync(
        string? bundleDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bundleDirectory) || !Directory.Exists(bundleDirectory))
            return null;

        DateTime localStamp = GetLocalBundleStamp(bundleDirectory);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, GitHubLatestRelease);
            request.Headers.UserAgent.ParseAdd("WhyNoDPI/" + GetCurrentVersion());
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using HttpResponseMessage response = await Http
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            await using Stream stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using JsonDocument document = await JsonDocument
                .ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!TryReadString(document.RootElement, "tag_name", out string tag))
                return null;
            if (!TryReadString(document.RootElement, "published_at", out string published) ||
                !DateTimeOffset.TryParse(published, out DateTimeOffset publishedOffset))
                return null;
            TryReadString(document.RootElement, "html_url", out string releaseUrl);

            DateTime publishedUtc = publishedOffset.UtcDateTime;
            // Запас в два дня: файлы внутри zip сохраняют дату сборки релиза,
            // а published_at может быть позже на несколько часов.
            bool isNewer = publishedUtc > localStamp.AddDays(2);

            return new ZapretUpdateInfo(
                tag,
                publishedUtc,
                localStamp,
                releaseUrl.Length == 0 ? null : releaseUrl,
                isNewer);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Дата локальной сборки пакета. Распакованный архив сохраняет время
    /// файлов, поэтому winws.exe — хороший ориентир.
    /// </summary>
    private static DateTime GetLocalBundleStamp(string root)
    {
        string[] candidates =
        {
            Path.Combine(root, "bin", "winws.exe"),
            Path.Combine(root, "bin", "WinDivert64.sys"),
        };

        DateTime[] stamps = candidates
            .Where(File.Exists)
            .Select(File.GetLastWriteTimeUtc)
            .ToArray();

        if (stamps.Length > 0)
            return stamps.Max();

        return Directory.GetCreationTimeUtc(root);
    }

    private static bool TryReadString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out JsonElement element))
            return false;
        if (element.ValueKind != JsonValueKind.String)
            return false;
        value = (element.GetString() ?? string.Empty).Trim();
        return true;
    }

    /// <summary>«1.2», «1.2.0» и «v1.2.0» сравниваются одинаково.</summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string cleaned = text.Trim().TrimStart('v', 'V');
        string[] parts = cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 4)
            return false;

        var numbers = new int[4];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out numbers[i]) || numbers[i] < 0)
                return false;
        }

        version = new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
        return true;
    }

    private static string GetCurrentVersion()
    {
        try
        {
            return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        }
        catch
        {
            return "1.0.0";
        }
    }
}
