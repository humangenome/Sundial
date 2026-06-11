using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Solarpunk.Launcher;

[SupportedOSPlatform("windows")]

/// <summary>
/// Boot-at-Windows-startup toggle. Backed by the per-user Run key so no admin
/// is required. Survives reboots; survives Windows Update.
///
/// Reference: Scout Voice (Tauri) uses a similar HKCU\...\Run entry; the
/// classic gotcha is that the value path must point to the installed Launcher
/// exe (not the staging dir), be properly quoted (Windows splits on spaces
/// otherwise), and include any startup flags. Pass --minimized so the
/// launcher starts to tray/minimized rather than popping a full window
/// every login.
/// </summary>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Solarpunk";
    private const string StartupArgs = "--minimized";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is not null;
        }
        catch
        {
            return false;
        }
    }

    public static bool Enable(string launcherExe, out string? error)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(launcherExe) || !File.Exists(launcherExe))
            {
                error = "launcher executable not found";
                return false;
            }

            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                ?? throw new InvalidOperationException("could not open HKCU Run key");

            // Quote the path so spaces in the install dir don't split it,
            // append minimized arg.
            key.SetValue(ValueName, $"\"{launcherExe}\" {StartupArgs}", RegistryValueKind.String);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool Disable(out string? error)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                error = null;
                return true;
            }
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Ensure the <c>solarpunk://</c> URL scheme handler is registered under
    /// HKCU\Software\Classes\solarpunk and points at the current launcher exe.
    /// Idempotent — runs on every launcher startup so customers who
    /// installed before v0.3.13 (when the NSIS installer first wrote the
    /// scheme) get it via auto-update too, and re-installs / path moves
    /// keep the command line current. No admin needed (HKCU only).
    ///
    /// Without this, panel links like
    /// <c>solarpunk://add?host=X&amp;port=Y&amp;label=Z</c> silently fail
    /// because Windows has no handler — a launcher installed before v0.3.13
    /// had no scheme entry, so clicking a join link did nothing because the HKCU entry
    /// was missing.
    /// </summary>
    public static void EnsureSolarpunkUrlSchemeRegistered(string launcherExe)
    {
        if (string.IsNullOrWhiteSpace(launcherExe) || !File.Exists(launcherExe))
            return;

        try
        {
            using (var root = Registry.CurrentUser.CreateSubKey(@"Software\Classes\solarpunk", writable: true))
            {
                if (root is null) return;
                root.SetValue(null, "URL:Solarpunk Protocol", RegistryValueKind.String);
                root.SetValue("URL Protocol", "", RegistryValueKind.String);
            }
            using (var iconKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\solarpunk\DefaultIcon", writable: true))
            {
                iconKey?.SetValue(null, launcherExe + ",0", RegistryValueKind.String);
            }
            using (var cmdKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\solarpunk\shell\open\command", writable: true))
            {
                cmdKey?.SetValue(null, $"\"{launcherExe}\" \"%1\"", RegistryValueKind.String);
            }
        }
        catch
        {
            // HKCU writes can be blocked by group policy or roaming-profile
            // weirdness on locked-down machines. Silent — the panel button
            // will keep prompting "no app to handle solarpunk:// links" and
            // the customer can reinstall via BeaconSetup.exe as a fallback.
        }
    }
}
