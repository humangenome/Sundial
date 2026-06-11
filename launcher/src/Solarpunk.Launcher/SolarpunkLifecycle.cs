using System.Reflection;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Solarpunk.Launcher;

[SupportedOSPlatform("windows")]

/// <summary>
/// Install lifecycle helpers — locate the install dir, version, and the
/// uninstaller for the "Remove Solarpunk" Settings action. Also exposes the
/// install-time version so the auto-updater knows what we're upgrading from.
///
/// On a side-loaded dev build (no installer ever ran), all of these return
/// fallback values so the launcher still functions for testing.
/// </summary>
public static class SolarpunkLifecycle
{
    private const string RegKeyPath = @"Software\HumanGenome\Solarpunk";
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Solarpunk";

    // CurrentVersion is the version of the RUNNING binary, not whatever the
    // NSIS installer last wrote to the registry. Reading from the registry
    // produced two bugs: (1) after auto-update, the UI still showed the
    // pre-update version because the registry wasn't rewritten, and
    // (2) the auto-updater's own version comparison saw the stale registry
    // value as the current version on every check, so it kept downloading
    // the same "new" version on every launcher close. Pull the version
    // straight from AssemblyInformationalVersion so it matches the actual
    // shipped assembly.
    // No registry fallback in the version path: the registry value is written
    // once at install and goes stale after every auto-update, which is exactly
    // what caused the re-download-on-every-close loop above. If the running
    // assembly has no version at all (a truly un-stamped local/side-load build)
    // report 0.0.0-dev so the UI and the update compare read "unknown dev
    // build", never a plausible-but-fake release number.
    public static string CurrentVersion =>
        AssemblyVersionString() ?? "0.0.0-dev";

    private static string? AssemblyVersionString()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            // Strip the "+commitsha" suffix MSBuild appends on default
            // release builds — we want just X.Y.Z for the version compare
            // and the UI line.
            var plus = info.IndexOf('+');
            var trimmed = plus > 0 ? info[..plus] : info;
            return trimmed;
        }
        return asm.GetName().Version?.ToString(3);
    }

    /// <summary>
    /// Install root (the directory that contains <c>app\Solarpunk.Launcher.exe</c>).
    /// Reads <c>HKCU\Software\HumanGenome\Solarpunk\InstallDir</c> first; that's
    /// what the NSIS installer writes. If the key is missing (early installs
    /// where the WriteRegStr silently failed, or NSIS aborted before the
    /// registry step), infer from <see cref="AppContext.BaseDirectory"/> —
    /// walk one parent up off <c>app\</c> to get the install root. Returns
    /// null for truly side-loaded builds where the .exe doesn't live under
    /// an <c>app\</c> subdirectory (e.g. <c>...\publish\Solarpunk.Launcher.exe</c>).
    /// </summary>
    public static string? InstallDir
    {
        get
        {
            var reg = ReadString(RegKeyPath, "InstallDir", null);
            if (!string.IsNullOrWhiteSpace(reg)) return reg;

            // Fallback: AppContext.BaseDirectory is normally
            // <installRoot>\app\ for an installed build. Walk up one. Only
            // accept the result if we're literally running from a directory
            // named "app" — that's how NSIS lays out the install and
            // matches what SolarpunkUpdater.cmd expects to swap.
            try
            {
                var baseDir = AppContext.BaseDirectory?.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.IsNullOrWhiteSpace(baseDir)) return null;

                var folderName = Path.GetFileName(baseDir);
                if (!string.Equals(folderName, "app", StringComparison.OrdinalIgnoreCase))
                    return null;

                var parent = Path.GetDirectoryName(baseDir);
                return string.IsNullOrWhiteSpace(parent) ? null : parent;
            }
            catch
            {
                return null;
            }
        }
    }

    public static string? UninstallerPath =>
        ReadString(UninstallKeyPath, "UninstallString", null)?.Trim('"');

    public static bool IsInstalled =>
        !string.IsNullOrWhiteSpace(InstallDir) && !string.IsNullOrWhiteSpace(UninstallerPath);

    /// <summary>
    /// Spawn the NSIS uninstaller. NSIS handles UI itself — once it returns,
    /// the launcher should exit (the uninstall script kills running Launcher
    /// processes too as a fallback).
    /// </summary>
    public static bool TryLaunchUninstaller(out string? error)
    {
        var path = UninstallerPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            error = "Uninstaller not found. Solarpunk may have been installed via a side-loaded build.";
            return false;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string? ReadString(string path, string name, string? fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(path, writable: false);
            return key?.GetValue(name) as string ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }
}
