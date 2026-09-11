using System.Runtime.Versioning;
using Microsoft.Win32;

namespace PlugBrowser.Core.Discovery;

/// <summary>Supplies the folders a scan walks: the platform's standard plugin locations, the two
/// registry-declared VST2 roots, and any folders the user has added.</summary>
public sealed class PluginPathProvider
{
    /// <summary>Folders VST3 hosts are required to look in.</summary>
    public static IEnumerable<string> DefaultVst3Roots()
    {
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "VST3");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Common", "VST3");
    }

    /// <summary>Conventional VST2 folders. VST2 has no standard location, so this is a best-effort list
    /// of the paths installers actually use, plus whatever the registry declares.</summary>
    public static IEnumerable<string> DefaultVst2Roots()
    {
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);

        yield return Path.Combine(common, "VST2");
        yield return Path.Combine(pf, "VSTPlugins");
        yield return Path.Combine(pf, "Steinberg", "VSTPlugins");
        yield return Path.Combine(pfx86, "VSTPlugins");
        yield return Path.Combine(pfx86, "Steinberg", "VSTPlugins");

        if (OperatingSystem.IsWindows())
        {
            foreach (var fromRegistry in RegistryVst2Roots())
                yield return fromRegistry;
        }
    }

    /// <summary>Reads <c>HKLM\SOFTWARE\VST\VSTPluginsPath</c> and its WOW6432Node twin, which is where
    /// installers record the folder the user chose. The 32-bit view is a distinct path, not a mirror.</summary>
    [SupportedOSPlatform("windows")]
    public static IEnumerable<string> RegistryVst2Roots()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            string? value = null;
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = baseKey.OpenSubKey(@"SOFTWARE\VST");
                value = key?.GetValue("VSTPluginsPath") as string;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // Registry unreadable under this account — the conventional folders still apply.
            }

            if (!string.IsNullOrWhiteSpace(value))
                yield return value.Trim();
        }
    }

    /// <summary>Every root to scan: the defaults plus <paramref name="userRoots"/>, de-duplicated
    /// case-insensitively and filtered to folders that actually exist.</summary>
    public static IReadOnlyList<string> ResolveRoots(IEnumerable<string>? userRoots = null)
    {
        var all = DefaultVst3Roots()
            .Concat(DefaultVst2Roots())
            .Concat(userRoots ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var root in all)
        {
            string full;
            try { full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar); }
            catch (ArgumentException) { continue; }
            catch (PathTooLongException) { continue; }

            if (seen.Add(full) && Directory.Exists(full))
                result.Add(full);
        }
        return result;
    }
}
