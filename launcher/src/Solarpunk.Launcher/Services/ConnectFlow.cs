using System.Diagnostics;
using Solarpunk.Launcher;

namespace Solarpunk.Launcher.Services;

// The v1 client connect flow, end to end. One method the UI calls with a typed
// "ip:port" target; it does the whole job and reports progress + a final result.
//
// Steps (this IS the v1 launcher's job):
//   1. Find the player's retail Solarpunk (Steam/Epic/MS Store scan).
//   2. Ensure the bundled UE4SS runtime is installed into the game's Win64 dir.
//   3. Ensure the bundled SolarpunkConnect mod is installed into UE4SS's Mods dir.
//   4. Write the connect-target file the SolarpunkConnect mod reads.
//   5. Launch retail Solarpunk (the mod issues "open ip:port"; the client travels in).
//
// Everything here is idempotent: a re-join skips already-installed UE4SS + mods
// and just rewrites the target + relaunches.
public sealed class ConnectFlow
{
    public enum Stage { Locating, InstallingRuntime, InstallingMod, WritingTarget, Launching, Done, Failed }

    public sealed record Progress(Stage Stage, string Message);

    public sealed record Result(bool Ok, string Message, int? GamePid = null);

    private readonly string? _gamePathOverride;

    public ConnectFlow(string? gamePathOverride = null)
    {
        _gamePathOverride = gamePathOverride;
    }

    public Result Connect(ConnectTargetService.Target target, Action<Progress>? onProgress = null)
    {
        void Report(Stage s, string m) => onProgress?.Invoke(new Progress(s, m));

        var overall = Stopwatch.StartNew();
        var phase = Stopwatch.StartNew();
        void PhaseDone(string name)
        {
            SolarpunkLog.Debug($"ConnectFlow: phase '{name}' took {phase.ElapsedMilliseconds} ms (total {overall.ElapsedMilliseconds} ms)");
            phase.Restart();
        }

        SolarpunkLog.Info($"ConnectFlow: begin target={target.Address} override={_gamePathOverride ?? "(none)"}");

        // 1. Locate retail Solarpunk.
        Report(Stage.Locating, "Finding your Solarpunk install...");
        var install = SolarpunkInstallLocator.Find(_gamePathOverride);
        if (install is null)
        {
            SolarpunkLog.Warn("ConnectFlow: locate failed — Solarpunk not found");
            return new Result(false,
                "Couldn't find Solarpunk. Install it via Steam, or set the game path in Settings.");
        }
        PhaseDone("locate");

        // 2. Locate the bundled client pack and install the UE4SS runtime.
        var pack = ClientPackLocator.FindClientPack();
        if (pack is null)
        {
            SolarpunkLog.Error("ConnectFlow: bundled UE4SS client pack not found");
            return new Result(false,
                "Solarpunk's bundled UE4SS client files are missing. Reinstall Solarpunk.");
        }
        SolarpunkLog.Debug($"ConnectFlow: client pack resolved -> {pack}");

        Report(Stage.InstallingRuntime, "Installing the Solarpunk mod runtime into Solarpunk...");
        var rt = Ue4ssBootstrapService.Install(pack, install.Win64Dir, ClientPackLocator.BundledVersion);
        if (rt.Status is Ue4ssBootstrapService.InstallStatus.Failed or Ue4ssBootstrapService.InstallStatus.SourceMissing
            || rt.ModsRoot is null)
        {
            SolarpunkLog.Error($"ConnectFlow: UE4SS runtime install failed: status={rt.Status} detail={rt.Detail}");
            var hint = OperatingSystem.IsWindows()
                ? " If this keeps happening, add the Solarpunk folder to Windows Defender exclusions and retry."
                : "";
            return new Result(false, $"Couldn't install the mod runtime: {rt.Detail}.{hint}");
        }
        PhaseDone("bootstrap");

        // 3. Install the bundled SolarpunkConnect mod into UE4SS's Mods dir.
        Report(Stage.InstallingMod, "Installing the Solarpunk join mod...");
        var modInstaller = new ModInstallService(rt.ModsRoot);
        var bundledMods = ClientPackLocator.BundledMods(pack);
        if (bundledMods.Count == 0)
        {
            SolarpunkLog.Error("ConnectFlow: no bundled mods found in client pack");
            return new Result(false, "Solarpunk's bundled join mod is missing from the client pack. Reinstall Solarpunk.");
        }

        var modSummary = modInstaller.InstallBundled(bundledMods);
        if (!modSummary.AllOk)
        {
            var failed = modSummary.Results.First(r =>
                r.Status is not ModInstallService.InstallStatus.Installed
                          and not ModInstallService.InstallStatus.Skipped);
            SolarpunkLog.Error($"ConnectFlow: mod install failed: {failed.Id} status={failed.Status} detail={failed.Detail}");
            return new Result(false, $"Couldn't install the join mod ({failed.Id}): {failed.Detail}.");
        }
        PhaseDone("mod-install");

        // 4. Write the connect-target file SolarpunkConnect reads + the client Engine.ini.
        Report(Stage.WritingTarget, $"Pointing Solarpunk at {target.Address}...");
        try
        {
            ConnectTargetService.Write(target);
        }
        catch (Exception ex)
        {
            SolarpunkLog.Error("ConnectFlow: write connect-target failed", ex);
            return new Result(false, $"Couldn't write the connect target: {ex.Message}.");
        }

        // The retail client MUST run OSS=Null + [OnlineSubsystemSteam] bEnabled=false
        // + stock IpNetDriver, or IpNetDriver tries to bind a SteamID instead of a
        // real UDP socket and the join dies with PendingNetGameCreateFailure
        // "Error initializing network layer" — this was THE validated Solarpunk
        // client-join blocker (live-proven 2026-06-09). UE can wipe the user
        // Engine.ini on a clean exit, so it is re-written on every Connect. Treated
        // as FATAL for Solarpunk: without this ini the join cannot succeed.
        try
        {
            WriteClientEngineIni();
            SolarpunkLog.Debug($"ConnectFlow: wrote client Engine.ini to {SolarpunkUserConfigDir()}");
        }
        catch (Exception ex)
        {
            // Unlike G2/Lantern (where the ini was best-effort), Solarpunk's join
            // is IMPOSSIBLE without this transport config — fail loudly instead of
            // launching a client that will sit at "Error initializing network layer".
            SolarpunkLog.Error("ConnectFlow: client Engine.ini write failed", ex);
            return new Result(false,
                $"Couldn't write the client network config ({ex.Message}). " +
                "Close Solarpunk if it's running and try again.");
        }
        PhaseDone("write-target");

        // 5. Launch retail Solarpunk.
        Report(Stage.Launching, "Launching Solarpunk...");
        var launchAtUtc = DateTime.UtcNow;
        var launch = GameLaunchService.Launch(install);
        if (!launch.Started)
        {
            SolarpunkLog.Error($"ConnectFlow: launch failed: {launch.Detail}");
            return new Result(false, $"Couldn't launch Solarpunk: {launch.Detail}.");
        }
        PhaseDone("launch");
        SolarpunkLog.Info($"ConnectFlow: done in {overall.ElapsedMilliseconds} ms total; game pid={launch.Pid?.ToString() ?? "?"}");

        // 5b. Inject UE4SS into the running game. The dwmapi proxy DOES work on the
        // current Solarpunk build (proven 2026-06-09), but proxy imports are fragile
        // across game updates (G2 build 0.41.0 silently dropped its dwmapi import),
        // so we belt-and-suspenders with the proven post-boot
        // CreateRemoteThread(LoadLibraryW) injection: if the proxy already loaded
        // UE4SS, the second LoadLibraryW is a no-op (module refcount bump); if the
        // proxy didn't load, this is the path that makes the join work. Without
        // UE4SS the SolarpunkConnect mod never runs and the client sits at the menu.
        var injectOk = true;
        var injectDetail = "skipped (non-Windows)";
        if (OperatingSystem.IsWindows())
        {
            Report(Stage.Launching, "Injecting the Solarpunk runtime into Solarpunk...");
            var ue4ssDll = Path.Combine(install.Win64Dir, "ue4ss", "UE4SS.dll");
            var ue4ssLog = Path.Combine(install.Win64Dir, "ue4ss", "UE4SS.log");
            var inj = Ue4ssInjector.InjectIntoGame(ue4ssDll, ue4ssLog, launchAtUtc);
            injectOk = inj.Ok;
            injectDetail = inj.Detail;
            SolarpunkLog.Info($"ConnectFlow: UE4SS inject ok={injectOk} detail={injectDetail}");
            PhaseDone("inject");
        }

        // Best-effort: surface the in-game UE4SS log so the Solarpunk.log shows
        // whether UE4SS injected + the mod ran. Never let this throw into the
        // connect flow — it's pure diagnostics.
        try
        {
            Ue4ssLogTail.SurfaceAfterLaunch(install.Win64Dir, launchAtUtc);
        }
        catch (Exception ex)
        {
            SolarpunkLog.Debug($"ConnectFlow: UE4SS log tail threw (ignored): {ex.Message}");
        }

        if (!injectOk)
        {
            Report(Stage.Done, $"Solarpunk launched, but the Solarpunk runtime didn't load: {injectDetail}");
            return new Result(false,
                $"Launched Solarpunk, but couldn't load the join runtime: {injectDetail}.", launch.Pid);
        }

        Report(Stage.Done, $"Solarpunk is connecting to {target.Address}...");
        return new Result(true, $"Joining {target.Address}.", launch.Pid);
    }

    // ------------------------------------------------------------------------
    // Client Engine.ini (the IpNetDriver transport swap — the join handshake
    // prerequisite). Lands in the Solarpunk user-scope config dir that a
    // Steam-launched retail client reads.
    // ------------------------------------------------------------------------

    // %LOCALAPPDATA%\Solarpunk\Saved\Config\Windows — where UE5 packaged games keep
    // the per-user Engine.ini (keyed off the project name "Solarpunk").
    private static string SolarpunkUserConfigDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Solarpunk", "Saved", "Config", "Windows");

    private static void WriteClientEngineIni()
    {
        var dir = SolarpunkUserConfigDir();
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Engine.ini"), ClientEngineIni);
    }

    // The PROVEN Solarpunk client transport config (live-validated 2026-06-09 —
    // a real retail Steam client joined the headless host with exactly this ini;
    // see solarpunk/launcher/Engine.ini.client and keep the two in lockstep).
    // [OnlineSubsystemSteam] bEnabled=false is THE load-bearing line: disabling
    // the Steam OSS deregisters SteamSockets as the default socket subsystem, so
    // the stock UIpNetDriver binds a real UDP socket instead of trying to bind a
    // SteamID. Without it the client fails with
    // ETravelFailure::PendingNetGameCreateFailure "Error initializing network layer".
    // OSS must therefore be Null (Steam OSS + SteamSockets are coupled), which is
    // why the character net-id is synthesized launcher-side (?Name= contract).
    private const string ClientEngineIni =
        """
        ; Solarpunk client Engine.ini — direct-IP join (written by the launcher on every Connect).
        ; Disables the Steam OSS (-> SteamSockets) so the stock UE5 IpNetDriver binds real UDP.
        ; Unreal can wipe this on a clean exit; it is re-applied each Connect.

        [OnlineSubsystem]
        DefaultPlatformService=Null

        [OnlineSubsystemSteam]
        bEnabled=false

        [OnlineSubsystemNull]
        bSimulateForwarded=true

        [/Script/Engine.GameEngine]
        !NetDriverDefinitions=ClearArray
        +NetDriverDefinitions=(DefName="GameNetDriver",DriverClassName="/Script/OnlineSubsystemUtils.IpNetDriver",DriverClassNameFallback="/Script/OnlineSubsystemUtils.IpNetDriver")

        [/Script/Engine.Engine]
        !NetDriverDefinitions=ClearArray
        +NetDriverDefinitions=(DefName="GameNetDriver",DriverClassName="/Script/OnlineSubsystemUtils.IpNetDriver",DriverClassNameFallback="/Script/OnlineSubsystemUtils.IpNetDriver")

        [/Script/OnlineSubsystemUtils.IpNetDriver]
        NetConnectionClassName=/Script/OnlineSubsystemUtils.IpConnection
        ConnectionTimeout=300.0
        InitialConnectTimeout=300.0
        MaxClientRate=2097152
        MaxInternetClientRate=2097152

        [/Script/Engine.Player]
        ConfiguredInternetSpeed=2097152
        ConfiguredLanSpeed=2097152

        [/Script/Engine.GameNetworkManager]
        TotalNetBandwidth=16777216
        MaxDynamicBandwidth=2097152
        MinDynamicBandwidth=524288
        """;
}
