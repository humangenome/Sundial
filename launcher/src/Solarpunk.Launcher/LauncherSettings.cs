using System.Text.Json;

namespace Solarpunk.Launcher;

// Solarpunk launcher settings persisted to %APPDATA%\Solarpunk\launcher-settings.json.
//
// PORTED FROM BEACON, trimmed for the Solarpunk (Solarpunk) client: Beacon's
// SN2 / UE4SS / Python path overrides and per-character map are dropped —
// the launcher locates Solarpunk + installs UE4SS via ConnectFlow, and
// CharacterStore owns the per-server character roster. The fields the ported MainWindow + AutoUpdater need
// (a manual game-path override, last address/endpoint, log dir, the two
// startup/update toggles) are kept.
public sealed class LauncherSettings
{
    private static readonly string SolarpunkAppDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Solarpunk");

    private static readonly string SettingsPath = Path.Combine(SolarpunkAppDirectory, "launcher-settings.json");

    // Manual override for the retail Solarpunk exe or its folder. Empty/null →
    // auto-detect via the Steam/Epic/MS Store scan (ConnectFlow + SolarpunkInstallLocator).
    public string? GamePathOverride { get; set; }

    // Last server address typed in, so the box pre-fills next session.
    public string? LastAddress { get; set; }

    // Endpoint (host:port) of the last selected server, restored on launch.
    public string? LastSelectedEndpoint { get; set; }

    // Optional override for where Solarpunk writes its log. Empty/null → the
    // default %APPDATA%\Solarpunk dir.
    public string? LogDirectory { get; set; }

    // Boot-at-startup toggle, mirrored to HKCU\...\Run by the installer /
    // Settings dialog. Default true so Solarpunk is reachable after a reboot.
    public bool StartOnWindowsLogin { get; set; } = true;

    // Whether to automatically check for + apply new launcher releases.
    public bool CheckForUpdatesOnStart { get; set; } = true;


    public string EffectiveLogDirectory =>
        string.IsNullOrWhiteSpace(LogDirectory) ? SolarpunkAppDirectory : LogDirectory!;

    public static LauncherSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new LauncherSettings();
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<LauncherSettings>(json) ?? new LauncherSettings();
        }
        catch
        {
            return new LauncherSettings();
        }
    }

    public void Save() => Save(out _);

    public bool Save(out string? error)
    {
        try
        {
            Directory.CreateDirectory(SolarpunkAppDirectory);
            Directory.CreateDirectory(EffectiveLogDirectory);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
