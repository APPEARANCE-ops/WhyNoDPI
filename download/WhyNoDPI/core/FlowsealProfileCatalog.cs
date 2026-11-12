using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NoDPI.Core;

/// <summary>
/// Imports Flowseal General files as plain-text argument templates once and
/// stores them in the user's WhyNoDPI cache. The app can then work without the
/// original BAT files. No command script is ever executed.
/// </summary>
public static class FlowsealProfileCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private sealed class CatalogData
    {
        public CatalogData() { }
        public string BundleRoot { get; set; } = string.Empty;
        public DateTime CreatedUtc { get; set; }
        public Dictionary<string, ProfileData> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ProfileData
    {
        public ProfileData() { }
        public string CommandTemplate { get; set; } = string.Empty;
        public long FileLength { get; set; }
        public long LastWriteUtcTicks { get; set; }
    }

    public static bool HasCache(string bundleRoot)
    {
        try { return File.Exists(GetCachePath(bundleRoot)); }
        catch { return false; }
    }

    public static IReadOnlyList<string> FindProfileNames(string bundleRoot)
    {
        CatalogData? catalog = LoadOrImport(bundleRoot);
        if (catalog is null || catalog.Profiles.Count == 0)
            return Array.Empty<string>();

        return catalog.Profiles.Keys
            .OrderBy(name => name.Equals("general.bat", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string GetCommandTemplate(string bundleRoot, string profileName)
    {
        CatalogData? catalog = LoadOrImport(bundleRoot);
        if (catalog is not null && catalog.Profiles.TryGetValue(Path.GetFileName(profileName), out ProfileData? profile) &&
            !string.IsNullOrWhiteSpace(profile.CommandTemplate))
            return profile.CommandTemplate;

        throw new FileNotFoundException(
            $"Профиль «{Path.GetFileName(profileName)}» не найден в кэше WhyNoDPI. Выберите папку полного пакета Flowseal, чтобы импортировать профили.");
    }

    private static CatalogData? LoadOrImport(string bundleRoot)
    {
        if (string.IsNullOrWhiteSpace(bundleRoot) || !Directory.Exists(bundleRoot))
            return null;

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bundleRoot));
        string cachePath = GetCachePath(root);
        CatalogData? cached = TryReadCache(cachePath, root);
        string[] profileFiles;
        try { profileFiles = Directory.GetFiles(root, "general*.bat", SearchOption.TopDirectoryOnly); }
        catch { profileFiles = Array.Empty<string>(); }

        // Once imported, profiles remain available even if the user removes the
        // original BAT definitions. If they are present, refresh the cache when
        // their names, sizes, or modification times change.
        if (cached is not null && (profileFiles.Length == 0 || IsCacheCurrent(cached, profileFiles)))
            return cached;
        if (profileFiles.Length == 0)
            return cached;

        var imported = new CatalogData
        {
            BundleRoot = root,
            CreatedUtc = DateTime.UtcNow
        };

        foreach (string file in profileFiles)
        {
            try
            {
                FileInfo info = new(file);
                string name = info.Name;
                imported.Profiles[name] = new ProfileData
                {
                    CommandTemplate = ExtractCommandTemplate(file),
                    FileLength = info.Length,
                    LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks
                };
            }
            catch
            {
                // Ignore malformed profile files but retain every valid profile.
            }
        }

        if (imported.Profiles.Count == 0)
            return cached;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            string tempPath = cachePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(imported, JsonOptions), new UTF8Encoding(false));
            File.Move(tempPath, cachePath, overwrite: true);
        }
        catch
        {
            // The app can still use the just-imported profiles for this session
            // if the local cache folder is not writable.
        }

        return imported;
    }

    private static CatalogData? TryReadCache(string cachePath, string root)
    {
        try
        {
            if (!File.Exists(cachePath))
                return null;
            CatalogData? catalog = JsonSerializer.Deserialize<CatalogData>(File.ReadAllText(cachePath));
            if (catalog is null || !string.Equals(catalog.BundleRoot, root, StringComparison.OrdinalIgnoreCase))
                return null;
            if (catalog.Profiles is null)
                return null;

            // JSON dictionaries are case-sensitive by default; restore our
            // case-insensitive lookup behavior after deserialization.
            catalog.Profiles = new Dictionary<string, ProfileData>(catalog.Profiles, StringComparer.OrdinalIgnoreCase);
            return catalog;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsCacheCurrent(CatalogData catalog, string[] currentFiles)
    {
        if (catalog.Profiles.Count != currentFiles.Length)
            return false;

        foreach (string file in currentFiles)
        {
            try
            {
                FileInfo info = new(file);
                if (!catalog.Profiles.TryGetValue(info.Name, out ProfileData? entry) ||
                    entry.FileLength != info.Length || entry.LastWriteUtcTicks != info.LastWriteTimeUtc.Ticks)
                    return false;
            }
            catch
            {
                return false;
            }
        }
        return true;
    }

    private static string ExtractCommandTemplate(string profilePath)
    {
        string[] lines = File.ReadAllLines(profilePath, Encoding.UTF8);
        var command = new StringBuilder();
        bool capture = false;
        bool continuation = false;

        foreach (string rawLine in lines)
        {
            string line = rawLine;
            if (!capture)
            {
                int exeIndex = line.IndexOf("winws.exe", StringComparison.OrdinalIgnoreCase);
                if (exeIndex < 0)
                    continue;

                capture = true;
                line = line[(exeIndex + "winws.exe".Length)..].TrimStart();
                // Remove the closing quote in "%BIN%winws.exe".
                if (line.StartsWith('"'))
                    line = line[1..].TrimStart();
            }
            else if (!continuation)
            {
                break;
            }

            string trimmed = line.TrimEnd();
            continuation = trimmed.EndsWith('^');
            if (continuation)
                trimmed = trimmed[..^1];
            command.Append(trimmed).Append(' ');
            if (!continuation)
                break;
        }

        if (!capture || command.Length == 0)
            throw new InvalidDataException("В файле профиля нет команды запуска winws.exe.");

        return command.ToString().Trim();
    }

    private static string GetCachePath(string bundleRoot)
    {
        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bundleRoot));
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized.ToUpperInvariant())));
        string baseFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WhyNoDPI", "profiles");
        return Path.Combine(baseFolder, hash + ".json");
    }
}
