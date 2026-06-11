namespace Solarpunk.Launcher.Services;

// Finds the bundled client UE4SS pack (dist/ue4ss-client) shipped beside the
// launcher, plus its bundled mods.
//
// PORT FROM BEACON: modeled on Beacon.Launcher/MainWindow.cs FindUe4ssRoot — same
// probe ladder (app/ue4ss, ../ue4ss, the dev dist/ue4ss-client tree, side-load
// path), renamed for Solarpunk. The installer (installer/solarpunk.nsi) lays the pack
// down at $INSTDIR\ue4ss AND $INSTDIR\app\ue4ss, so the first two probes cover an
// installed launcher and the dist probes cover running from a dev build output.
public static class ClientPackLocator
{
    // Version string for the bundled client pack. Bumping this forces a UE4SS +
    // mod reinstall into the player's game on next connect (idempotency key for
    // Ue4ssBootstrapService + ModInstallService). Keep in lockstep with launcher
    // releases that change the bundled UE4SS/mod payload.
    public const string BundledVersion = "1.0.1";

    // The id ModInstallService records SolarpunkConnect under (folder name in the
    // game's UE4SS Mods dir).
    public const string SolarpunkConnectModId = "SolarpunkConnect";

    // Resolve the bundled client pack root (the folder that contains UE4SS.dll
    // and a Mods/ subtree). Returns null if no pack is found.
    public static string? FindClientPack()
    {
        var here = AppContext.BaseDirectory;
        var candidates = new[]
        {
            // Installed layout: launcher at $INSTDIR\app\, pack copied into
            // $INSTDIR\app\ue4ss (and $INSTDIR\ue4ss one level up).
            Path.Combine(here, "ue4ss"),
            Path.Combine(here, "..", "ue4ss"),
            // Dev layout: build output under solarpunk/launcher/src/
            // Solarpunk.Launcher/bin/<cfg>/net8.0-windows[/win-x64/publish],
            // repo pack at solarpunk/launcher/dist/ue4ss-client. 5 ups covers
            // a plain build, 7 ups covers a win-x64 publish output.
            Path.Combine(here, "..", "..", "..", "..", "..", "dist", "ue4ss-client"),
            Path.Combine(here, "..", "..", "..", "..", "..", "..", "dist", "ue4ss-client"),
            Path.Combine(here, "..", "..", "..", "..", "..", "..", "..", "dist", "ue4ss-client"),
            // Side-load fallback.
            @"C:\Solarpunk\ue4ss",
            @"C:\Solarpunk\client-ue4ss",
        };

        return candidates
            .Select(SafeFullPath)
            .Where(p => p is not null)
            .FirstOrDefault(p => Directory.Exists(p!) && HasUe4ssDll(p!));
    }

    // Enumerate the mods shipped in the bundled pack's Mods folder as
    // ModInstallService.BundledMod entries (one per top-level mod dir).
    public static IReadOnlyList<ModInstallService.BundledMod> BundledMods(string clientPackDir)
    {
        var modsDir = Ue4ssBootstrapService.BundledModsDir(clientPackDir);
        if (modsDir is null) return Array.Empty<ModInstallService.BundledMod>();

        var list = new List<ModInstallService.BundledMod>();
        foreach (var dir in Directory.EnumerateDirectories(modsDir))
        {
            var id = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(id)) continue;
            list.Add(new ModInstallService.BundledMod(id, dir, BundledVersion));
        }
        return list;
    }

    private static bool HasUe4ssDll(string dir)
    {
        if (File.Exists(Path.Combine(dir, "UE4SS.dll"))) return true;
        // Tolerate a one-level-nested pack.
        return File.Exists(Path.Combine(dir, "ue4ss", "UE4SS.dll"));
    }

    private static string? SafeFullPath(string p)
    {
        try { return Path.GetFullPath(p); } catch { return null; }
    }
}
