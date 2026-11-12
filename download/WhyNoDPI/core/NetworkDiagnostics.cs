using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Net;
using System.Net.Http;

namespace NoDPI.Core;

public sealed record ServiceCheckResult(
    string Service,
    bool IsReachable,
    string Details,
    int? HttpStatusCode,
    long ElapsedMilliseconds);

public sealed class NetworkDiagnostics
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(7);

    public async Task<IReadOnlyList<ServiceCheckResult>> CheckImportantServicesAsync(CancellationToken cancellationToken = default)
    {
        ServiceCheckResult[] results = await Task.WhenAll(
            CheckAsync("YouTube", "https://www.youtube.com/generate_204", cancellationToken),
            CheckAsync("Discord", "https://discord.com/api/v10/gateway", cancellationToken),
            CheckAsync("Telegram", "https://web.telegram.org/", cancellationToken));
        return results;
    }

    private static async Task<ServiceCheckResult> CheckAsync(string service, string url, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = true,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            AutomaticDecompression = DecompressionMethods.All
        };
        using var client = new HttpClient(handler) { Timeout = RequestTimeout };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("WhyNoDPI/1.0 (+network diagnostics)");
        request.Headers.Accept.ParseAdd("*/*");

        try
        {
            using HttpResponseMessage response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            stopwatch.Stop();
            int status = (int)response.StatusCode;
            bool reachable = status >= 200 && status < 400;
            string details = reachable
                ? $"Доступен · HTTP {status} · {stopwatch.ElapsedMilliseconds} мс"
                : $"Ответ получен, но сайт вернул HTTP {status} ({response.StatusCode})";
            return new ServiceCheckResult(service, reachable, details, status, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new ServiceCheckResult(service, false, $"Тайм-аут · более {RequestTimeout.TotalSeconds:0} с", null, stopwatch.ElapsedMilliseconds);
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            string reason = ex.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => "ошибка DNS",
                HttpRequestError.ConnectionError => "ошибка соединения или TLS",
                HttpRequestError.SecureConnectionError => "ошибка TLS",
                _ => "ошибка HTTP-соединения"
            };
            return new ServiceCheckResult(service, false, $"Недоступен · {reason}: {ex.Message}", null, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new ServiceCheckResult(service, false, $"Проверка не пройдена: {ex.Message}", null, stopwatch.ElapsedMilliseconds);
        }
    }
}
