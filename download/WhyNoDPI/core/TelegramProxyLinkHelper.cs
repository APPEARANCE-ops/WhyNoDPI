using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace NoDPI.Core;

/// <summary>Validates and converts official Telegram MTProxy deep links.</summary>
public static class TelegramProxyLinkHelper
{
    public static bool TryParse(string? input, out string server, out int port, out string secret, out string error)
    {
        server = string.Empty;
        port = 0;
        secret = string.Empty;
        error = string.Empty;

        string value = (input ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            error = "Вставь ссылку конкретного MTProto-прокси из канала. Ссылка на сам канал не содержит адрес сервера.";
            return false;
        }

        if (!value.Contains("://", StringComparison.Ordinal))
            value = "https://" + value.TrimStart('/');

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            error = "Не удалось разобрать ссылку. Поддерживаются t.me/proxy?... и tg://proxy?... .";
            return false;
        }

        bool isTelegramDeepLink = uri.Scheme.Equals("tg", StringComparison.OrdinalIgnoreCase) &&
                                  uri.Host.Equals("proxy", StringComparison.OrdinalIgnoreCase);
        bool isTelegramWebLink = (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
                                  uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)) &&
                                 (uri.Host.Equals("t.me", StringComparison.OrdinalIgnoreCase) ||
                                  uri.Host.Equals("telegram.me", StringComparison.OrdinalIgnoreCase)) &&
                                 uri.AbsolutePath.Trim('/').Equals("proxy", StringComparison.OrdinalIgnoreCase);

        if (!isTelegramDeepLink && !isTelegramWebLink)
        {
            error = "Это не ссылка на прокси Telegram. Нужна ссылка t.me/proxy?... или tg://proxy?...; ссылка на канал @mtp4tg открывает список серверов.";
            return false;
        }

        Dictionary<string, string> query = ParseQuery(uri.Query);
        if (!query.TryGetValue("server", out string? parsedServer) || string.IsNullOrWhiteSpace(parsedServer) ||
            !query.TryGetValue("port", out string? portValue) || !int.TryParse(portValue, out port) ||
            !query.TryGetValue("secret", out string? parsedSecret) || string.IsNullOrWhiteSpace(parsedSecret))
        {
            error = "В ссылке отсутствуют обязательные параметры server, port или secret.";
            server = string.Empty;
            port = 0;
            secret = string.Empty;
            return false;
        }

        server = parsedServer;
        secret = parsedSecret;

        if (!TryBuild(server, port, secret, out _, out error))
        {
            server = string.Empty;
            port = 0;
            secret = string.Empty;
            return false;
        }

        return true;
    }

    public static bool TryBuild(string server, int port, string secret, out string telegramUri, out string error)
    {
        telegramUri = string.Empty;
        error = string.Empty;
        server = (server ?? string.Empty).Trim();
        secret = (secret ?? string.Empty).Trim();

        if (server.Length == 0 || server.Length > 253 || Uri.CheckHostName(server.Trim('[', ']')) == UriHostNameType.Unknown)
        {
            error = "Укажи корректный домен или IP-адрес MTProxy-сервера.";
            return false;
        }

        if (port is < 1 or > 65535)
        {
            error = "Порт должен быть числом от 1 до 65535.";
            return false;
        }

        if (secret.Length < 32 || secret.Length > 512 || secret.Length % 2 != 0 ||
            !Regex.IsMatch(secret, "\\A[0-9a-fA-F]+\\z", RegexOptions.CultureInvariant))
        {
            error = "Secret должен быть шестнадцатеричной строкой чётной длины (обычно начинается с ee и содержит не менее 32 символов).";
            return false;
        }

        telegramUri = $"tg://proxy?server={Uri.EscapeDataString(server)}&port={port}&secret={Uri.EscapeDataString(secret)}";
        return true;
    }

    public static string ToWebLink(string server, int port, string secret) =>
        $"https://t.me/proxy?server={Uri.EscapeDataString(server)}&port={port}&secret={Uri.EscapeDataString(secret)}";

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = part.Split('=', 2);
            string key = Uri.UnescapeDataString(pair[0].Replace('+', ' '));
            string value = pair.Length > 1 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : string.Empty;
            values[key] = value;
        }
        return values;
    }
}
