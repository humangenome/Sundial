using System.Diagnostics;
using Solarpunk.Launcher;

namespace Solarpunk.Launcher.Services;

// Launches the player's retail Solarpunk. The SolarpunkConnect UE4SS mod (already
// installed into the game's Win64 dir) reads the connect-target file the launcher
// wrote and issues "open <ip>:<port>" once the game is at the menu, so this
// service's only job is to start the process with UE4SS auto-loading.
//
// PORT FROM BEACON: Beacon spawned SN2 under a Frida injector to load UE4SS. The
// Solarpunk v1 client uses the standard UE4SS proxy-DLL install (Ue4ssBootstrapService
// drops dwmapi.dll next to the exe), so launching is just a plain Process.Start of
// the shipping exe — Windows auto-loads UE4SS via the proxy. We drop steam_appid.txt
// beside the exe so the game gets its Steam identity (matches the validated
// direct-exe launch in docs/RUNTIME.md).
public static class GameLaunchService
{
    public sealed record LaunchResult(bool Started, int? Pid, string? Detail);

    // Start the game from <paramref name="install"/>. Drops steam_appid.txt
    // (1805110) next to the exe first. Working directory is the exe's Win64 dir
    // so the UE4SS proxy + UE4SS.dll resolve. Steam must already be running and
    // logged in; we do not gate on it here (a logged-out Steam shows its own
    // re-login prompt, which is an account state, not a launcher fault).
    public static LaunchResult Launch(SolarpunkInstallLocator.GameInstall install)
    {
        if (install is null)
        {
            SolarpunkLog.Error("GameLaunch: no game install");
            return new LaunchResult(false, null, "no game install");
        }
        if (!File.Exists(install.ExePath))
        {
            SolarpunkLog.Error($"GameLaunch: exe missing at {install.ExePath}");
            return new LaunchResult(false, null, $"exe missing at {install.ExePath}");
        }

        TryDropSteamAppId(install.Win64Dir);

        SolarpunkLog.Info($"GameLaunch: starting exe={install.ExePath} cwd={install.Win64Dir}");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = install.ExePath,
                WorkingDirectory = install.Win64Dir,
                UseShellExecute = false,
            };
            var proc = Process.Start(psi);
            if (proc is null)
            {
                SolarpunkLog.Error("GameLaunch: Process.Start returned null");
                return new LaunchResult(false, null, "Process.Start returned null");
            }
            SolarpunkLog.Info($"GameLaunch: started pid={proc.Id}");
            return new LaunchResult(true, proc.Id, null);
        }
        catch (Exception ex)
        {
            SolarpunkLog.Error($"GameLaunch: Process.Start threw for {install.ExePath}", ex);
            return new LaunchResult(false, null, ex.Message);
        }
    }

    // True if a Solarpunk shipping process is already running. Lets the UI warn
    // before launching a second copy.
    public static bool IsGameRunning()
    {
        // The Steam retail build runs as SolarpunkSteam-Win64-Shipping (the "Steam"
        // infix); the bare/WinGDK names are fallbacks. Without the Steam name the UI
        // never sees the game as running (no "Close Solarpunk", Connect stays enabled).
        foreach (var name in new[]
        {
            "SolarpunkSteam-Win64-Shipping", "Solarpunk-Win64-Shipping",
            "SolarpunkSteam-WinGDK-Shipping", "Solarpunk-WinGDK-Shipping",
        })
        {
            try { if (Process.GetProcessesByName(name).Length > 0) return true; }
            catch { /* ignore and continue */ }
        }
        return false;
    }

    private static void TryDropSteamAppId(string win64Dir)
    {
        try
        {
            var path = Path.Combine(win64Dir, "steam_appid.txt");
            // Only (re)write if missing or stale so we don't needlessly touch the
            // game dir on every launch.
            var want = SolarpunkInstallLocator.SteamAppId.ToString();
            if (!File.Exists(path) || (SafeRead(path)?.Trim() != want))
                File.WriteAllText(path, want);
        }
        catch { /* best-effort — the game may already have one */ }
    }

    private static string? SafeRead(string path)
    {
        try { return File.ReadAllText(path); } catch { return null; }
    }
}
