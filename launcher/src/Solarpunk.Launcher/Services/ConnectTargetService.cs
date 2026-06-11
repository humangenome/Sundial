using System.Net;
using System.Text.RegularExpressions;
using Solarpunk.Launcher;

namespace Solarpunk.Launcher.Services;

// Writes the connect-target file that the SolarpunkConnect client mod reads,
// plus the client-identity file the (future) native net-id hook reads.
//
// CONTRACT — must match dist/ue4ss-client/Mods/SolarpunkConnect/Scripts/main.lua:
//   TARGET_FILE = %APPDATA%\Solarpunk\connect-target.txt
//   format      = a single line "<ip>:<port>?Name=<charname>" (the mod strips
//                 whitespace then issues the stock console "open <line>").
// The ?Name= option is the SERVER-side contract: the headless host's
// InitNewPlayer hook (SolarpunkPlugin.dll) reads it from the login options to
// mint the synthetic per-character SteamID64 that keys save persistence.
// The mod polls until a PlayerController exists, so writing the file just
// before launch (and the mod loading once the game starts) is the whole
// handshake. Clearing it on disconnect prevents a stale target from
// auto-rejoining a later session.
//
// IDENTITY FILE — %APPDATA%\Solarpunk\client-identity.txt, one line:
//   STEAM <steamid64> <charHash8>
// where steamid64 = 76561190000000000 + CRC32(lowercase charname) (the same
// formula the server-side hook uses — see IMPLEMENTATION-SPEC.md "Synthetic
// id") and charHash8 = the CRC32 as 8 hex chars. Written per-connect so the
// native client hook (ULocalPlayer::GetPreferredUniqueNetId, scoped but not
// currently required) has a stable identity source if it ever ships.
public static class ConnectTargetService
{
    public static string SolarpunkAppDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Solarpunk");

    public static string TargetFilePath => Path.Combine(SolarpunkAppDir, "connect-target.txt");

    public static string IdentityFilePath => Path.Combine(SolarpunkAppDir, "client-identity.txt");

    // Base of the synthetic SteamID64 space (7656119xxxxxxxxxx, valid Steam
    // format so the game's CreateSteamIDFromString accepts it). Keep in
    // lockstep with the server hook (solarpunk/native/SolarpunkPlugin) and
    // host_netid_enforcer.lua.
    public const ulong SyntheticSteamIdBase = 76561190000000000UL;

    public sealed record Target(string Host, int Port, string? CharacterName = null, string? JoinPassword = null)
    {
        public string Address => $"{Host}:{Port}";

        // The exact line handed to the in-game "open" command by the mod.
        public string ConnectLine
        {
            get
            {
                var name = SanitizeCharacterName(CharacterName);
                if (string.IsNullOrEmpty(name)) return Address;
                // Join password rides the NAME channel as "<char>__SPPW__<pw>".
                // ?Password= is dead: the server-side gate (SolarpunkAuth) cannot
                // read FString *properties* (RequestURL / FURL.Op) from Lua — only
                // strings returned by a UFunction (GetPlayerName) — so the password
                // is appended to the name and SolarpunkAuth strips the __SPPW__
                // token before deriving identity (the name channel is the only
                // URL option the Lua gate can read back reliably).
                var pw = SanitizePasswordToken(JoinPassword);
                if (pw.Length > 0) name = $"{name}__SPPW__{pw}";
                return $"{Address}?Name={name}";
            }
        }
    }

    // Character names ride inside a UE travel URL, so strip anything that
    // would break option parsing (?, =, &, /, whitespace) and cap the length.
    // Keep it permissive otherwise — players name characters creatively.
    public static string SanitizeCharacterName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var cleaned = new string(raw.Trim()
            .Where(c => !char.IsControl(c) && c is not ('?' or '=' or '&' or '/' or '\\' or ':' or ' '))
            .ToArray());
        return cleaned.Length > 32 ? cleaned[..32] : cleaned;
    }

    // Join password also rides the travel URL (in the name channel), so it must
    // survive option parsing: strip the same URL-structural chars the name does.
    // The server gate (SolarpunkAuth) matches the surviving value byte-exact and
    // does NOT lowercase, so passwords stay case-sensitive; the panel must reject
    // those structural chars in a join password. Cap longer than a name.
    public static string SanitizePasswordToken(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var cleaned = new string(raw
            .Where(c => !char.IsControl(c) && c is not ('?' or '=' or '&' or '/' or '\\' or ':' or ' '))
            .ToArray());
        return cleaned.Length > 64 ? cleaned[..64] : cleaned;
    }

    // Stable synthetic SteamID64 for a character name: base + CRC32(lowercase
    // name). Mirrors the server-side InitNewPlayer hook so launcher-side
    // tooling (identity file, support logs) and the host agree on the id.
    public static ulong SyntheticSteamId64(string characterName) =>
        SyntheticSteamIdBase + Crc32(System.Text.Encoding.UTF8.GetBytes(
            SanitizeCharacterName(characterName).ToLowerInvariant()));

    public static string CharacterHash8(string characterName) =>
        Crc32(System.Text.Encoding.UTF8.GetBytes(
            SanitizeCharacterName(characterName).ToLowerInvariant()))
            .ToString("x8");

    // Parse a user-entered "ip:port" (or "host:port", or bare "host" defaulting
    // to the gameplay port). Returns null if it can't be parsed into a usable
    // host + port. Default port mirrors Solarpunk's base gameplay port 7777.
    public static Target? Parse(string? input, int defaultPort = 7777)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = input.Trim();

        // Strip a leading scheme if the player pasted a solarpunk:// link body.
        s = Regex.Replace(s, "^[a-zA-Z]+://", "");
        s = s.Trim().TrimEnd('/');

        // Bracketed IPv6 literal: [::1]:7777
        var v6 = Regex.Match(s, @"^\[(?<h>[^\]]+)\](?::(?<p>\d{1,5}))?$");
        if (v6.Success)
        {
            var host = v6.Groups["h"].Value;
            var port = v6.Groups["p"].Success ? int.Parse(v6.Groups["p"].Value) : defaultPort;
            return ValidPort(port) && IPAddress.TryParse(host, out _) ? new Target(host, port) : null;
        }

        var idx = s.LastIndexOf(':');
        // Treat as host:port only if there's exactly one colon (an unbracketed
        // bare IPv6 has several — reject it; require brackets for IPv6).
        if (idx > 0 && s.IndexOf(':') == idx)
        {
            var host = s[..idx].Trim();
            var portStr = s[(idx + 1)..].Trim();
            if (host.Length == 0) return null;
            if (!int.TryParse(portStr, out var port) || !ValidPort(port)) return null;
            return new Target(host, port);
        }

        // No port supplied → host with the default gameplay port.
        return s.Length > 0 ? new Target(s, defaultPort) : null;
    }

    // Write "<ip>:<port>?Name=<char>\n" to the target file the mod reads, and
    // (when a character name is set) the client-identity file. Creates
    // %APPDATA%\Solarpunk if needed. Atomic (writes .tmp then renames).
    // Returns the target-file path.
    public static string Write(Target target)
    {
        Directory.CreateDirectory(SolarpunkAppDir);
        var path = TargetFilePath;
        var line = target.ConnectLine;
        AtomicWrite(path, line + "\r\n");
        SolarpunkLog.Debug($"ConnectTarget: wrote '{line}' to {path}");

        var name = SanitizeCharacterName(target.CharacterName);
        if (!string.IsNullOrEmpty(name))
        {
            var identity = $"STEAM {SyntheticSteamId64(name)} {CharacterHash8(name)}";
            AtomicWrite(IdentityFilePath, identity + "\r\n");
            SolarpunkLog.Debug($"ConnectTarget: wrote identity '{identity}' to {IdentityFilePath}");
        }
        return path;
    }

    private static void AtomicWrite(string path, string content)
    {
        var tmp = path + ".tmp";
        // CRLF terminator — the mod's f:read("*l") + whitespace strip handles
        // either, but Windows tooling reads CRLF cleanly.
        File.WriteAllText(tmp, content);
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
    }

    // Remove the target file so a future game launch doesn't auto-rejoin a stale
    // server. Best-effort. (The identity file is intentionally left — it's the
    // player's stable character identity, not a per-session routing artifact.)
    public static void Clear()
    {
        try
        {
            if (File.Exists(TargetFilePath))
            {
                File.Delete(TargetFilePath);
                SolarpunkLog.Debug($"ConnectTarget: cleared {TargetFilePath}");
            }
        }
        catch { /* best-effort */ }
    }

    private static bool ValidPort(int port) => port is > 0 and <= 65535;

    // ---------------------------------------------------------------------
    // CRC32 (IEEE 802.3, reflected, poly 0xEDB88320) — table-based, no
    // package dependency. Must match the server-side hook's CRC32 so the
    // synthetic ids agree.
    // ---------------------------------------------------------------------
    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    public static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
            crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
