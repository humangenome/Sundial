using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Solarpunk.Launcher;

namespace Solarpunk.Launcher.Services;

// Installs the SolarpunkConnect client mod into a UE4SS Mods folder, idempotently.
//
// PORT FROM BEACON: this is Beacon.Launcher/ModInstallService.cs adapted for the
// Solarpunk CLIENT. Beacon's version was manifest/HTTP-driven (download each mod
// zip off the CDN, verify the pinned sha256, atomic-extract). The Solarpunk v1
// client ships SolarpunkConnect bundled in the installer (dist/ue4ss-client/Mods),
// so the primary path here copies that bundled mod folder into the game's UE4SS
// Mods dir. The proven zip-slip-safe extract + streamed SHA-256 + idempotent
// installed.json state machinery is preserved verbatim so a future hosted-manifest
// path can reuse it without rewriting the safety code.
//
// Idempotency contract (same as Beacon): a re-join with the same bundled version
// is a no-op once installed.json records the mod as present and the target dir
// exists. Bumping the bundled launcher (new mod version string) reinstalls.
public sealed class ModInstallService
{
    private const long MaxModBytes = 256L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public enum InstallStatus
    {
        Skipped,
        Installed,
        SourceMissing,
        HashMismatch,
        ExtractFailed,
        SizeExceeded,
    }

    public sealed record ModInstallResult(
        string Id,
        string Version,
        InstallStatus Status,
        string? Path,
        string? Detail);

    public sealed record InstallSummary(
        IReadOnlyList<ModInstallResult> Results)
    {
        public bool AllOk => Results.All(r =>
            r.Status == InstallStatus.Skipped || r.Status == InstallStatus.Installed);
    }

    // Describes one bundled mod to install: the source folder shipped beside the
    // launcher (dist/ue4ss-client/Mods/<Id>) and the version string the bundled
    // pack is pinned to. Version is what drives the idempotent "already installed"
    // check — bump it when the bundled mod changes so it reinstalls.
    public sealed record BundledMod(string Id, string SourceDir, string Version);

    private readonly string _modsRoot;

    // <param name="ue4ssModsRoot">The UE4SS "Mods" directory inside the player's
    // retail game install (e.g. ...\Solarpunk\Binaries\Win64\ue4ss\Mods). Each
    // bundled mod lands as a child folder named after its id.</param>
    public ModInstallService(string ue4ssModsRoot)
    {
        if (string.IsNullOrWhiteSpace(ue4ssModsRoot))
            throw new ArgumentException("UE4SS Mods root is required.", nameof(ue4ssModsRoot));
        _modsRoot = ue4ssModsRoot;
    }

    public string ModsRoot => _modsRoot;

    // Copy each bundled mod folder into the UE4SS Mods dir, idempotently.
    // Updates installed.json as it goes. Returns one result per entry.
    public InstallSummary InstallBundled(IReadOnlyList<BundledMod> mods)
    {
        Directory.CreateDirectory(_modsRoot);
        var state = LoadState(_modsRoot);
        var results = new List<ModInstallResult>(mods.Count);

        SolarpunkLog.Debug($"Mods: installing {mods.Count} bundled mod(s) into {_modsRoot}");
        foreach (var mod in mods)
        {
            var result = InstallOneBundled(state, mod);
            results.Add(result);
            SolarpunkLog.Debug($"Mods: {mod.Id} v{mod.Version} status={result.Status} path={result.Path ?? "(none)"} detail={result.Detail} source={mod.SourceDir}");

            if (result.Status == InstallStatus.Installed)
            {
                state.Mods[mod.Id] = new InstalledMod
                {
                    Version = mod.Version,
                    Source = mod.SourceDir,
                    InstalledUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                };
                state.UpdatedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                SaveState(_modsRoot, state);
            }
        }

        return new InstallSummary(results);
    }

    private ModInstallResult InstallOneBundled(InstallState state, BundledMod mod)
    {
        var targetDir = Path.Combine(_modsRoot, SanitizeId(mod.Id));

        if (!Directory.Exists(mod.SourceDir))
        {
            return new ModInstallResult(mod.Id, mod.Version, InstallStatus.SourceMissing, null,
                $"bundled source not found at {mod.SourceDir}");
        }

        // Already installed at this version and the folder is present → skip.
        if (state.Mods.TryGetValue(mod.Id, out var existing)
            && string.Equals(existing.Version, mod.Version, StringComparison.Ordinal)
            && Directory.Exists(targetDir))
        {
            return new ModInstallResult(mod.Id, mod.Version, InstallStatus.Skipped, targetDir, "already installed");
        }

        var stagingDir = targetDir + ".new";
        var backupDir = targetDir + ".old";
        TryDeleteDir(stagingDir);
        TryDeleteDir(backupDir);

        try
        {
            Directory.CreateDirectory(stagingDir);
            CopyDirectory(mod.SourceDir, stagingDir);
        }
        catch (Exception ex)
        {
            TryDeleteDir(stagingDir);
            return new ModInstallResult(mod.Id, mod.Version, InstallStatus.ExtractFailed, null, ex.Message);
        }

        // Atomic swap: move the live dir aside, move staging into place, drop
        // the backup. Roll back on a mid-swap failure so we never leave the
        // game with a half-written mod folder.
        try
        {
            if (Directory.Exists(targetDir)) Directory.Move(targetDir, backupDir);
            Directory.Move(stagingDir, targetDir);
            TryDeleteDir(backupDir);
        }
        catch (Exception ex)
        {
            try { if (Directory.Exists(backupDir) && !Directory.Exists(targetDir)) Directory.Move(backupDir, targetDir); }
            catch { /* nothing better to do */ }
            TryDeleteDir(stagingDir);
            return new ModInstallResult(mod.Id, mod.Version, InstallStatus.ExtractFailed, null, $"swap failed: {ex.Message}");
        }

        return new ModInstallResult(mod.Id, mod.Version, InstallStatus.Installed, targetDir, "copied from bundled pack");
    }

    // Install a mod from a downloaded/staged zip. Retained from Beacon so a
    // future hosted-manifest path (per-server mod overrides) can reuse the exact
    // zip-slip-safe + hash-verified extract without reimplementing the safety.
    // Not wired into the v1 client connect flow.
    public ModInstallResult InstallFromZip(string id, string version, string zipPath, string? expectedSha256)
    {
        var targetDir = Path.Combine(_modsRoot, SanitizeId(id));
        Directory.CreateDirectory(_modsRoot);

        long bytes;
        try { bytes = new FileInfo(zipPath).Length; }
        catch (Exception ex) { return new ModInstallResult(id, version, InstallStatus.SourceMissing, null, ex.Message); }

        if (bytes > MaxModBytes)
            return new ModInstallResult(id, version, InstallStatus.SizeExceeded, null, $"{bytes} bytes exceeds {MaxModBytes}");

        if (!string.IsNullOrEmpty(expectedSha256))
        {
            var actual = Sha256File(zipPath);
            SolarpunkLog.Debug($"Mods: {id} v{version} zip sha256={actual} (expected {expectedSha256})");
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                SolarpunkLog.Error($"Mods: {id} v{version} sha mismatch — expected {expectedSha256}, got {actual}");
                return new ModInstallResult(id, version, InstallStatus.HashMismatch, null,
                    $"expected {expectedSha256}, got {actual}");
            }
        }

        var stagingDir = targetDir + ".new";
        var backupDir = targetDir + ".old";
        TryDeleteDir(stagingDir);
        TryDeleteDir(backupDir);

        try
        {
            Directory.CreateDirectory(stagingDir);
            ExtractZipSafe(zipPath, stagingDir);
        }
        catch (Exception ex)
        {
            TryDeleteDir(stagingDir);
            return new ModInstallResult(id, version, InstallStatus.ExtractFailed, null, ex.Message);
        }

        try
        {
            if (Directory.Exists(targetDir)) Directory.Move(targetDir, backupDir);
            Directory.Move(stagingDir, targetDir);
            TryDeleteDir(backupDir);
        }
        catch (Exception ex)
        {
            try { if (Directory.Exists(backupDir) && !Directory.Exists(targetDir)) Directory.Move(backupDir, targetDir); }
            catch { }
            TryDeleteDir(stagingDir);
            return new ModInstallResult(id, version, InstallStatus.ExtractFailed, null, $"swap failed: {ex.Message}");
        }

        return new ModInstallResult(id, version, InstallStatus.Installed, targetDir, $"{bytes} bytes");
    }

    // Recursive directory copy, overwrite-on-collision. Used for the bundled
    // copy path (no zip involved). Rejects nothing — the source is our own
    // bundled pack, not untrusted input.
    public static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var dir in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, dir);
            Directory.CreateDirectory(Path.Combine(destDir, rel));
        }
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, file);
            var outPath = Path.Combine(destDir, rel);
            var outDir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
            File.Copy(file, outPath, overwrite: true);
        }
    }

    // Streamed SHA-256 over a file. 64 KB chunks so a large mod doesn't pin the
    // whole file in memory. Carried from Beacon.
    public static string Sha256File(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    // Extract a zip into destDir, rejecting zip-slip (parent traversal, absolute
    // paths). Throws on any violation. Carried verbatim from Beacon.
    public static void ExtractZipSafe(string zipPath, string destDir)
    {
        var destFull = Path.GetFullPath(destDir);
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.FullName)) continue;
            var rel = entry.FullName.Replace('\\', '/').TrimStart('/');
            if (rel.Contains("..")) throw new InvalidDataException($"zip-slip rejected: {entry.FullName}");
            if (Path.IsPathRooted(rel)) throw new InvalidDataException($"absolute path rejected: {entry.FullName}");

            var outPath = Path.GetFullPath(Path.Combine(destFull, rel));
            if (!outPath.StartsWith(destFull + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !outPath.Equals(destFull, StringComparison.Ordinal))
                throw new InvalidDataException($"path escapes destination: {entry.FullName}");

            if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(outPath);
                continue;
            }
            var dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            entry.ExtractToFile(outPath, overwrite: true);
        }
    }

    private static string SanitizeId(string id)
    {
        var safe = new string(id.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
        return string.IsNullOrEmpty(safe) ? "mod" : safe;
    }

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* best-effort */ }
    }

    private static InstallState LoadState(string modsRoot)
    {
        var path = Path.Combine(modsRoot, "solarpunk-installed.json");
        if (!File.Exists(path)) return new InstallState();
        try
        {
            var json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize<InstallState>(json, JsonOpts);
            if (state is null) return new InstallState();
            state.Mods ??= new Dictionary<string, InstalledMod>(StringComparer.Ordinal);
            return state;
        }
        catch
        {
            // A corrupted state file must not block a reinstall — the caller
            // sees mods install once and the file gets rewritten clean.
            return new InstallState();
        }
    }

    private static void SaveState(string modsRoot, InstallState state)
    {
        var path = Path.Combine(modsRoot, "solarpunk-installed.json");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOpts));
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
    }

    private sealed class InstallState
    {
        [JsonPropertyName("updated_unix")] public long UpdatedUnix { get; set; }
        [JsonPropertyName("schema")]       public int Schema { get; set; } = 1;
        [JsonPropertyName("mods")]         public Dictionary<string, InstalledMod> Mods { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class InstalledMod
    {
        [JsonPropertyName("version")]        public string Version { get; set; } = "";
        [JsonPropertyName("source")]         public string Source { get; set; } = "";
        [JsonPropertyName("installed_unix")] public long InstalledUnix { get; set; }
    }
}
