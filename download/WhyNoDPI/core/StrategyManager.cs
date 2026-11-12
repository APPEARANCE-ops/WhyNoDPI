using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NoDPI.Core;

public static class StrategyManager
{
    public static IReadOnlyList<string> FindProfiles(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return Array.Empty<string>();

        try
        {
            string root = EngineManager.ResolveBundleRoot(directory);
            return FlowsealProfileCatalog.FindProfileNames(root);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static string DisplayName(string fileName)
    {
        string name = Path.GetFileNameWithoutExtension(fileName);
        if (!name.StartsWith("general", StringComparison.OrdinalIgnoreCase))
            return name;

        string suffix = name["general".Length..].Trim().Trim('(', ')', ' ', '-');
        return string.IsNullOrWhiteSpace(suffix) ? "Основная" : suffix;
    }
}
