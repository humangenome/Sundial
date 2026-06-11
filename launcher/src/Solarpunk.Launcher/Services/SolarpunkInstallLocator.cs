using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Solarpunk.Launcher;

namespace Solarpunk.Launcher.Services;

// Locates the player's retail Solarpunk install: the shipping exe and its
// ...\Binaries\Win64 directory (where UE4SS's proxy DLL must land).
//
// PORT FROM BEACON: this is the SN2 FindSn2Exe / EnumerateSteamLibraries /
// EnumerateEpicInstallLocations / EnumerateMsStoreInstallLocations logic from
// Beacon.Launcher/MainWindow.cs, pulled into a standalone service and renamed for
// Solarpunk. Steam AppID 1805110, game folder "Solarpunk", UE project folder
// "Solarpunk". The Steam retail exe is SolarpunkSteam-Win64-Shipping.exe (the "Steam"
// infix is the headless host's process name too); the non-Steam/WinGDK variants are tolerated.
public static class SolarpunkInstallLocator
{
    public const int SteamAppId = 1805110;
    // The real Steam retail shipping exe carries a "Steam" infix; the bare
    // Solarpunk-Win64-Shipping.exe is the generic fallback. Both Win64 + WinGDK.
    public const string SteamWin64ExeName = "SolarpunkSteam-Win64-Shipping.exe";
    public const string Win64ExeName = "Solarpunk-Win64-Shipping.exe";
    public const string SteamGdkExeName = "SolarpunkSteam-WinGDK-Shipping.exe";
    public const string WinGdkExeName = "Solarpunk-WinGDK-Shipping.exe";

    // Every shipping-exe name we recognize, most-likely first.
    private static readonly string[] ExeNames =
    {
        SteamWin64ExeName, Win64ExeName, SteamGdkExeName, WinGdkExeName,
    };

    public sealed record GameInstall(string ExePath, string Win64Dir);

    // Resolve the game install. <paramref name="overridePath"/> (a launcher
    // setting) wins if it points at the exe directly or a folder containing it.
    // Otherwise scan Steam libraries, then Epic, then MS Store. Returns null if
    // nothing is found.
    public static GameInstall? Find(string? overridePath = null)
    {
        var exe = FindExe(overridePath);
        if (exe is null)
        {
            SolarpunkLog.Debug("SolarpunkInstallLocator: no install found");
            return null;
        }
        var win64 = Path.GetDirectoryName(exe);
        if (string.IsNullOrEmpty(win64))
        {
            SolarpunkLog.Debug($"SolarpunkInstallLocator: exe '{exe}' has no parent dir; treating as not found");
            return null;
        }
        SolarpunkLog.Info($"SolarpunkInstallLocator: found Solarpunk exe={exe} win64={win64}");
        return new GameInstall(exe, win64);
    }

    private static string? FindExe(string? overridePath)
    {
        var normalizedOverride = NormalizePath(overridePath);
        SolarpunkLog.Debug($"SolarpunkInstallLocator: searching (override={normalizedOverride ?? "(none)"})");
        if (normalizedOverride is not null)
        {
            if (File.Exists(normalizedOverride))
            {
                SolarpunkLog.Debug($"SolarpunkInstallLocator: override is a file and exists -> {normalizedOverride}");
                return normalizedOverride;
            }
            if (Directory.Exists(normalizedOverride))
            {
                SolarpunkLog.Debug($"SolarpunkInstallLocator: override is a dir; scanning inside {normalizedOverride}");
                var hit = EnumerateInsideRoot(normalizedOverride).FirstOrDefault(File.Exists);
                if (hit is not null)
                {
                    SolarpunkLog.Debug($"SolarpunkInstallLocator: override dir hit -> {hit}");
                    return hit;
                }
                SolarpunkLog.Debug("SolarpunkInstallLocator: override dir had no recognized exe");
            }
            else
            {
                SolarpunkLog.Debug($"SolarpunkInstallLocator: override path does not exist: {normalizedOverride}");
            }
        }

        var candidates = new List<string>();

        // Steam: enumerate every library via libraryfolders.vdf, then check each
        // for Solarpunk in the standard UE5 packaged layout.
        foreach (var lib in EnumerateSteamLibraries())
        {
            SolarpunkLog.Debug($"SolarpunkInstallLocator: Steam library enumerated -> {lib}");
            var root = Path.Combine(lib, "steamapps", "common", "Solarpunk");
            candidates.AddRange(EnumerateInsideRoot(root));
        }

        // Static fallback sweep for the unusual case (registry/vdf unreadable).
        foreach (var root in new[]
        {
            @"C:\Program Files (x86)\Steam\steamapps\common\Solarpunk",
            @"D:\SteamLibrary\steamapps\common\Solarpunk",
            @"D:\Steam\steamapps\common\Solarpunk",
            @"E:\Steam\steamapps\common\Solarpunk",
        })
        {
            candidates.AddRange(EnumerateInsideRoot(root));
        }

        // Epic Games Launcher manifests.
        foreach (var dir in EnumerateEpicInstallLocations())
        {
            SolarpunkLog.Debug($"SolarpunkInstallLocator: Epic install location -> {dir}");
            candidates.AddRange(EnumerateInsideRoot(dir));
        }

        // Microsoft Store / Game Pass (cheap scan; skipped if no matching pkg).
        foreach (var dir in EnumerateMsStoreInstallLocations())
        {
            SolarpunkLog.Debug($"SolarpunkInstallLocator: MS Store install location -> {dir}");
            candidates.AddRange(EnumerateInsideRoot(dir));
        }

        var hitExe = candidates.FirstOrDefault(File.Exists);
        if (hitExe is null)
        {
            SolarpunkLog.Debug($"SolarpunkInstallLocator: no candidate exe existed (checked {candidates.Count} candidate path(s))");
            if (SolarpunkLog.Verbose)
                foreach (var c in candidates.Distinct())
                    SolarpunkLog.Debug($"SolarpunkInstallLocator:   tried {c}");
        }
        return hitExe;
    }

    // Walk an arbitrary install root and yield any Solarpunk shipping exe in the
    // usual UE5 packaged layout. Tolerant of nesting differences between Steam /
    // Epic / MS Store distributions. "Solarpunk" is Solarpunk's UE project name.
    private static IEnumerable<string> EnumerateInsideRoot(string root)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) yield break;
        // Binaries subdirs across Steam / Epic / MS Store packaged layouts, ×
        // every recognized exe name (Steam infix + generic, Win64 + WinGDK).
        var binDirs = new[]
        {
            @"Solarpunk\Binaries\Win64",
            @"Content\Solarpunk\Binaries\Win64",
            @"Binaries\Win64",
            @"Solarpunk\Binaries\WinGDK",
            @"Content\Solarpunk\Binaries\WinGDK",
            @"Binaries\WinGDK",
        };
        foreach (var dir in binDirs)
            foreach (var exe in ExeNames)
            {
                var p = Path.Combine(root, dir, exe);
                if (File.Exists(p)) yield return p;
            }
        // Also accept an exe sitting directly in the root.
        foreach (var exe in ExeNames)
        {
            var p = Path.Combine(root, exe);
            if (File.Exists(p)) yield return p;
        }
    }

    private static IEnumerable<string> EnumerateSteamLibraries()
    {
        var steamRoots = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"),
        };

        if (OperatingSystem.IsWindows())
        {
            foreach (var rootFromRegistry in ReadSteamRegistryRoots())
                steamRoots.Insert(0, rootFromRegistry);
        }

        foreach (var steam in steamRoots)
        {
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;

            string text;
            try { text = File.ReadAllText(vdf); }
            catch { continue; }

            foreach (Match m in Regex.Matches(text, "\"path\"\\s*\"(?<p>[^\"]+)\""))
            {
                var path = m.Groups["p"].Value.Replace("\\\\", "\\");
                if (Directory.Exists(path)) yield return path;
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> ReadSteamRegistryRoots()
    {
        var roots = new List<string>();
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string regPath && !string.IsNullOrWhiteSpace(regPath))
                roots.Add(regPath.Replace('/', '\\'));
        }
        catch { }
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            if (key?.GetValue("InstallPath") is string regPath && !string.IsNullOrWhiteSpace(regPath))
                roots.Add(regPath);
        }
        catch { }
        return roots;
    }

    private static IEnumerable<string> EnumerateEpicInstallLocations()
    {
        var manifestRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifestRoot)) yield break;

        string[] files;
        try { files = Directory.GetFiles(manifestRoot, "*.item"); }
        catch { yield break; }

        foreach (var file in files)
        {
            string body;
            try { body = File.ReadAllText(file); }
            catch { continue; }

            if (!Regex.IsMatch(body, "solarpunk", RegexOptions.IgnoreCase)) continue;

            var m = Regex.Match(body, "\"InstallLocation\"\\s*:\\s*\"(?<p>[^\"]+)\"");
            if (!m.Success) continue;
            var path = m.Groups["p"].Value.Replace("\\\\", "\\");
            if (Directory.Exists(path)) yield return path;
        }
    }

    private static IEnumerable<string> EnumerateMsStoreInstallLocations()
    {
        var ps = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"),
        };
        var exe = ps.FirstOrDefault(File.Exists);
        if (exe is null) yield break;

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(
            "Get-AppxPackage -ErrorAction SilentlyContinue | " +
            "Where-Object { $_.Name -match 'Solarpunk' -or $_.PackageFamilyName -match 'Solarpunk' } | " +
            "ForEach-Object { $_.InstallLocation }");

        string output;
        try
        {
            using var p = Process.Start(psi);
            if (p is null) yield break;
            if (!p.WaitForExit(6_000))
            {
                try { p.Kill(true); } catch { }
                yield break;
            }
            output = p.StandardOutput.ReadToEnd();
        }
        catch { yield break; }

        foreach (var line in output.Split('\n'))
        {
            var path = line.Trim();
            if (string.IsNullOrEmpty(path)) continue;
            if (Directory.Exists(path)) yield return path;
        }
    }

    private static string? NormalizePath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
}
