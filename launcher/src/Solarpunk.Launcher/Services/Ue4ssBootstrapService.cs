using Solarpunk.Launcher;

namespace Solarpunk.Launcher.Services;

// Installs the bundled UE4SS client runtime into the player's retail Solarpunk
// Win64 directory (next to Solarpunk-Win64-Shipping.exe), idempotently, and
// exposes the helper that patches UE4SS-settings.ini for an out-of-tree mods
// folder.
//
// PORT FROM BEACON: Beacon's Ue4ssBootstrapService.cs only did the INI patch
// (Beacon injected UE4SS via Frida at launch, so it never copied UE4SS into the
// game). The Solarpunk v1 client uses the STANDARD UE4SS proxy-DLL install instead:
// the launcher drops the bundled UE4SS pack (UE4SS.dll + dwmapi.dll proxy + Mods/)
// straight into the game's Win64 dir, so Windows auto-loads UE4SS when the game
// starts. The INI-patch transform (ConfigurePerInstanceModsPath / Rewrite) is
// carried over verbatim for the case where mods live outside the game tree.
//
// The install is idempotent: it only overwrites the UE4SS runtime files when the
// bundled version differs from what's already installed (a marker file records
// the installed version). The bundled SolarpunkConnect mod itself is dropped by
// ModInstallService, not here, so the two concerns stay separate.
public static class Ue4ssBootstrapService
{
    // The UE4SS proxy DLL filename. UE4SS ships as a renamed system-DLL proxy
    // that Windows loads from the game's exe directory at process start. dwmapi
    // is the proxy Solarpunk's host overlay uses (see dist RUNTIME.md), so the
    // client matches it. If the bundled pack ships a different proxy name we
    // copy whatever DLL is present at the pack root.
    public const string ProxyDllName = "dwmapi.dll";

    // Records which bundled UE4SS version is installed in a given game dir so a
    // re-launch with the same version is a no-op. Lives beside UE4SS.dll.
    private const string VersionMarkerName = "solarpunk-ue4ss-version.txt";

    public enum InstallStatus
    {
        Skipped,
        Installed,
        SourceMissing,
        Failed,
    }

    public sealed record InstallResult(InstallStatus Status, string? Ue4ssRoot, string? ModsRoot, string? Detail);

    // Install (or refresh) the UE4SS runtime into <paramref name="gameWin64Dir"/>.
    //
    // <param name="clientPackDir">The bundled client UE4SS pack shipped beside the
    // launcher (dist/ue4ss-client). Must contain UE4SS.dll; may contain a proxy
    // DLL (dwmapi.dll), UE4SS-settings.ini, and a Mods/ subtree.</param>
    // <param name="gameWin64Dir">The retail game's ...\Binaries\Win64 directory,
    // i.e. the folder that holds Solarpunk-Win64-Shipping.exe.</param>
    // <param name="bundledVersion">Version string of the bundled pack; drives the
    // idempotent skip when it matches the installed marker.</param>
    //
    // UE4SS lands directly in the Win64 dir (the proxy DLL must sit next to the
    // exe to be auto-loaded). UE4SS then scans <c>&lt;Win64&gt;\ue4ss\Mods</c> for
    // mods. Returns the resolved ue4ss root + Mods root so the caller can hand the
    // Mods root to <see cref="ModInstallService"/>.
    public static InstallResult Install(string clientPackDir, string gameWin64Dir, string bundledVersion)
    {
        SolarpunkLog.Debug($"UE4SS: install start pack={clientPackDir} win64={gameWin64Dir} version={bundledVersion}");
        if (string.IsNullOrWhiteSpace(clientPackDir) || !Directory.Exists(clientPackDir))
        {
            SolarpunkLog.Error($"UE4SS: client pack not found at {clientPackDir}");
            return new InstallResult(InstallStatus.SourceMissing, null, null, $"client pack not found at {clientPackDir}");
        }
        if (string.IsNullOrWhiteSpace(gameWin64Dir) || !Directory.Exists(gameWin64Dir))
        {
            SolarpunkLog.Error($"UE4SS: game Win64 dir not found at {gameWin64Dir}");
            return new InstallResult(InstallStatus.SourceMissing, null, null, $"game Win64 dir not found at {gameWin64Dir}");
        }

        var sourceDll = FindUe4ssDll(clientPackDir);
        if (sourceDll is null)
        {
            SolarpunkLog.Error($"UE4SS: UE4SS.dll missing from bundled pack {clientPackDir}");
            return new InstallResult(InstallStatus.SourceMissing, null, null, $"UE4SS.dll missing from bundled pack {clientPackDir}");
        }
        SolarpunkLog.Debug($"UE4SS: source UE4SS.dll={sourceDll}");

        // UE4SS proxy + dll live IN the Win64 dir; everything else (settings,
        // signatures, Mods) lives under <Win64>\ue4ss so we don't litter the
        // game's exe folder with our subtree.
        var ue4ssRoot = Path.Combine(gameWin64Dir, "ue4ss");
        var modsRoot = Path.Combine(ue4ssRoot, "Mods");

        // The effective pack root is wherever UE4SS.dll actually lives (tolerates
        // a one-level-nested pack). The mirror plan covers the ENTIRE pack tree
        // recursively — every file in every subdir (UE4SS_Signatures, Mods,
        // anything added later). A hand-picked copy list is exactly the bug that
        // dropped UE4SS_Signatures\ and made the UE5.7 built-in AOB scan time out
        // (Fatal "PS scan timed out" -> UE4SS never initialized -> no auto-join).
        var packRoot = Path.GetDirectoryName(Path.GetFullPath(sourceDll))!;
        var sourceProxy = FindProxyDll(packRoot) ?? FindProxyDll(clientPackDir);
        var plan = BuildMirrorPlan(packRoot, ue4ssRoot, gameWin64Dir, sourceProxy);
        if (sourceProxy is null)
        {
            SolarpunkLog.Warn($"UE4SS: no proxy DLL found in pack {clientPackDir} (expected {ProxyDllName}); UE4SS may not auto-load");
        }

        var marker = Path.Combine(ue4ssRoot, VersionMarkerName);
        var installedVersion = SafeReadText(marker)?.Trim();
        var missing = plan.Where(e => !File.Exists(e.Dest)).ToList();
        if (missing.Count == 0
            && Directory.Exists(modsRoot)
            && File.Exists(marker)
            && string.Equals(installedVersion, bundledVersion, StringComparison.Ordinal))
        {
            SolarpunkLog.Info($"UE4SS: already installed (version {bundledVersion}, all {plan.Count} pack files present) in {ue4ssRoot}; skipping copy");
            return new InstallResult(InstallStatus.Skipped, ue4ssRoot, modsRoot, "already installed");
        }
        SolarpunkLog.Info($"UE4SS: installing version {bundledVersion} into {ue4ssRoot} (marker had '{installedVersion ?? "(none)"}', {missing.Count} pack file(s) missing on disk)");

        try
        {
            Directory.CreateDirectory(ue4ssRoot);
            Directory.CreateDirectory(modsRoot);

            foreach (var entry in plan)
            {
                // Don't clobber a UE4SS-settings.ini we may have patched in place
                // (ConfigurePerInstanceModsPath rewrites it after install).
                if (entry.OnlyIfMissing && File.Exists(entry.Dest))
                {
                    SolarpunkLog.Debug($"UE4SS: kept existing {Path.GetFileName(entry.Dest)} (pack copy not applied)");
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(entry.Dest)!);
                File.Copy(entry.Source, entry.Dest, overwrite: true);
                SolarpunkLog.Debug($"UE4SS: copied {Path.GetRelativePath(packRoot, entry.Source)} -> {entry.Dest}");
            }

            File.WriteAllText(marker, bundledVersion);
            SolarpunkLog.Info($"UE4SS: install complete (version {bundledVersion}); modsRoot={modsRoot}");
            return new InstallResult(InstallStatus.Installed, ue4ssRoot, modsRoot, "installed bundled UE4SS runtime");
        }
        catch (Exception ex)
        {
            SolarpunkLog.Error($"UE4SS: install failed into {ue4ssRoot}", ex);
            return new InstallResult(InstallStatus.Failed, ue4ssRoot, modsRoot, ex.Message);
        }
    }

    // The Mods dir the bundled pack ships (dist/ue4ss-client/Mods), so the caller
    // can enumerate the bundled mods to hand to ModInstallService. Null if the
    // pack has no Mods folder.
    public static string? BundledModsDir(string clientPackDir)
    {
        var mods = Path.Combine(clientPackDir, "Mods");
        return Directory.Exists(mods) ? mods : null;
    }

    // One file the recursive mirror will copy: pack source path -> install dest
    // path. OnlyIfMissing marks files whose installed copy must be preserved
    // (UE4SS-settings.ini, which ConfigurePerInstanceModsPath patches in place).
    private sealed record MirrorEntry(string Source, string Dest, bool OnlyIfMissing);

    // Enumerate the ENTIRE pack tree (all files, all subdirs) and map each file
    // to its install destination:
    //   - the proxy DLL at the pack root -> <Win64>\<proxy>.dll (must sit next to
    //     the game exe so Windows auto-loads it)
    //   - everything else -> <Win64>\ue4ss\<same relative path> (UE4SS.dll,
    //     UE4SS-settings.ini, UE4SS_Signatures\*, Mods\*, future additions)
    //   - docs (.md) and logs (.log) and our version marker are skipped
    // The same plan drives both the idempotent skip check (every Dest must exist)
    // and the copy loop, so a file the mirror would lay down can never be silently
    // dropped or silently missing.
    private static List<MirrorEntry> BuildMirrorPlan(string packRoot, string ue4ssRoot, string gameWin64Dir, string? sourceProxy)
    {
        var plan = new List<MirrorEntry>();
        var proxyFull = sourceProxy is null ? null : Path.GetFullPath(sourceProxy);

        foreach (var file in Directory.EnumerateFiles(packRoot, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(name, VersionMarkerName, StringComparison.OrdinalIgnoreCase)) continue;

            if (proxyFull is not null
                && string.Equals(Path.GetFullPath(file), proxyFull, StringComparison.OrdinalIgnoreCase))
            {
                plan.Add(new MirrorEntry(file, Path.Combine(gameWin64Dir, name), OnlyIfMissing: false));
                continue;
            }

            var rel = Path.GetRelativePath(packRoot, file);
            var onlyIfMissing = string.Equals(rel, "UE4SS-settings.ini", StringComparison.OrdinalIgnoreCase);
            plan.Add(new MirrorEntry(file, Path.Combine(ue4ssRoot, rel), onlyIfMissing));
        }

        // A nested pack (packRoot = <clientPackDir>\ue4ss) can keep its proxy at
        // the OUTER root; the enumeration above won't see it, so add it here.
        if (proxyFull is not null && !plan.Any(e =>
                string.Equals(Path.GetFullPath(e.Source), proxyFull, StringComparison.OrdinalIgnoreCase)))
        {
            plan.Add(new MirrorEntry(sourceProxy!, Path.Combine(gameWin64Dir, Path.GetFileName(sourceProxy!)), OnlyIfMissing: false));
        }

        return plan;
    }

    private static string? FindUe4ssDll(string packDir)
    {
        var direct = Path.Combine(packDir, "UE4SS.dll");
        if (File.Exists(direct)) return direct;
        // Tolerate a one-level-nested pack (some archives wrap in ue4ss/).
        var nested = Path.Combine(packDir, "ue4ss", "UE4SS.dll");
        return File.Exists(nested) ? nested : null;
    }

    private static string? FindProxyDll(string packDir)
    {
        var preferred = Path.Combine(packDir, ProxyDllName);
        if (File.Exists(preferred)) return preferred;
        // Accept any of the well-known UE4SS proxy names if the pack uses a
        // different one.
        foreach (var name in new[] { "xinput1_3.dll", "dinput8.dll", "dwmapi.dll" })
        {
            var p = Path.Combine(packDir, name);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static string? SafeReadText(string path)
    {
        try { return File.ReadAllText(path); } catch { return null; }
    }

    // ---------------------------------------------------------------------
    // UE4SS-settings.ini +ModsFolderPaths patch — carried over from Beacon for
    // the case where mods live OUTSIDE the game tree (e.g. a per-server cache).
    // Not on the v1 happy path (the bundled mod installs into <Win64>\ue4ss\Mods,
    // which UE4SS scans by default), but kept intact + tested.
    // ---------------------------------------------------------------------

    private const string ManagedMarker = "; --- SOLARPUNK-MANAGED (do not edit; rewritten on each connect) ---";
    private const string ManagedEndMarker = "; --- /SOLARPUNK-MANAGED ---";

    // Update <c>&lt;ue4ssRoot&gt;/UE4SS-settings.ini</c> to expose
    // <paramref name="perInstanceModsAbsPath"/> to UE4SS's mod scanner. Pass null
    // to clear any prior managed override. Idempotent; atomic (writes .new then
    // renames). No-op if the ini doesn't exist.
    public static void ConfigurePerInstanceModsPath(string ue4ssRoot, string? perInstanceModsAbsPath)
    {
        if (string.IsNullOrWhiteSpace(ue4ssRoot)) return;
        var iniPath = Path.Combine(ue4ssRoot, "UE4SS-settings.ini");
        if (!File.Exists(iniPath))
        {
            SolarpunkLog.Debug($"UE4SS: INI patch skipped — no UE4SS-settings.ini at {iniPath}");
            return;
        }

        var original = File.ReadAllText(iniPath);
        var rewritten = Rewrite(original, perInstanceModsAbsPath);
        if (string.Equals(original, rewritten, StringComparison.Ordinal))
        {
            SolarpunkLog.Debug($"UE4SS: INI already current at {iniPath}; no patch needed");
            return;
        }

        var tmp = iniPath + ".new";
        File.WriteAllText(tmp, rewritten);
        if (File.Exists(iniPath)) File.Delete(iniPath);
        File.Move(tmp, iniPath);
        SolarpunkLog.Debug($"UE4SS: patched mods path in {iniPath} (modsPath={perInstanceModsAbsPath ?? "(cleared)"})");
    }

    // Pure transform — split out so tests can round-trip without touching the
    // filesystem. Carried over from Beacon verbatim (marker text renamed).
    public static string Rewrite(string iniText, string? perInstanceModsAbsPath)
    {
        var lines = iniText.Replace("\r\n", "\n").Split('\n').ToList();
        StripManagedBlock(lines);

        if (string.IsNullOrWhiteSpace(perInstanceModsAbsPath))
            return JoinPreservingEol(lines, iniText);

        var normalized = perInstanceModsAbsPath.Replace('\\', '/');

        var block = new List<string>
        {
            ManagedMarker,
            $"+ModsFolderPaths = {normalized}",
            ManagedEndMarker,
        };

        var overridesIdx = FindSectionLineIndex(lines, "[Overrides]");
        if (overridesIdx < 0)
        {
            // No [Overrides] section exists. Synthesize one, but keep the header
            // INSIDE the managed block so a later strip removes it cleanly —
            // otherwise a second Rewrite would see a leftover [Overrides] header
            // and take the append branch instead, accumulating blank lines and
            // breaking idempotency.
            var prefix = new List<string>
            {
                ManagedMarker,
                "[Overrides]",
                $"+ModsFolderPaths = {normalized}",
                ManagedEndMarker,
                "",
            };
            lines.InsertRange(0, prefix);
        }
        else
        {
            var insertAt = lines.Count;
            for (int i = overridesIdx + 1; i < lines.Count; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("[", StringComparison.Ordinal)
                    && trimmed.EndsWith("]", StringComparison.Ordinal))
                {
                    insertAt = i;
                    break;
                }
            }
            while (insertAt > 0 && string.IsNullOrWhiteSpace(lines[insertAt - 1]))
                insertAt--;
            block.Insert(0, "");
            lines.InsertRange(insertAt, block);
        }

        return JoinPreservingEol(lines, iniText);
    }

    private static void StripManagedBlock(List<string> lines)
    {
        for (int i = 0; i < lines.Count;)
        {
            if (lines[i].TrimEnd() == ManagedMarker)
            {
                int end = i + 1;
                while (end < lines.Count && lines[end].TrimEnd() != ManagedEndMarker) end++;
                if (end < lines.Count) end++;
                lines.RemoveRange(i, end - i);
                if (i > 0 && string.IsNullOrWhiteSpace(lines[i - 1])
                    && (i >= lines.Count || string.IsNullOrWhiteSpace(lines[i]) ||
                        (lines[i].TrimStart().StartsWith("[") && lines[i].TrimStart().EndsWith("]"))))
                {
                    lines.RemoveAt(i - 1);
                }
                else if (i == 0 && i < lines.Count && string.IsNullOrWhiteSpace(lines[i]))
                {
                    // The block was synthesized at the top of the file with a
                    // trailing blank separator; collapse that blank so removing
                    // the block restores the original leading content exactly.
                    lines.RemoveAt(i);
                }
                continue;
            }
            i++;
        }
    }

    private static int FindSectionLineIndex(List<string> lines, string headerWithBrackets)
    {
        for (int i = 0; i < lines.Count; i++)
            if (string.Equals(lines[i].Trim(), headerWithBrackets, StringComparison.Ordinal))
                return i;
        return -1;
    }

    private static string JoinPreservingEol(List<string> lines, string originalText)
    {
        var eol = originalText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return string.Join(eol, lines);
    }
}
