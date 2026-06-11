using System.Text.Json;
using System.Text.Json.Serialization;

namespace Solarpunk.Launcher;

/// <summary>
/// Per-server character roster. Each entry pairs a friendly character name with
/// its synthetic Steam-shaped identity (the launcher mints
/// 76561190000000000 + CRC32(lowercase name) — see ConnectTargetService).
///
/// PORT FROM BEACON (Beacon.Launcher/CharacterStore.cs), adapted for the
/// Solarpunk ?Name= contract: the character NAME is the identity input — the
/// headless host's InitNewPlayer hook derives the save-shard key from the
/// ?Name= login option, so two characters with the same name on the same
/// server are the SAME save slot by design (stable reconnect = the point).
///
/// Stored at <c>%APPDATA%\Solarpunk\characters.json</c>.
/// </summary>
public static class CharacterStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Solarpunk", "characters.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public sealed record Character(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("server_endpoint")] string ServerEndpoint,
        [property: JsonPropertyName("steam_id64")] ulong SteamId64,
        [property: JsonPropertyName("char_hash8")] string CharHash8,
        [property: JsonPropertyName("last_played")] DateTimeOffset? LastPlayed,
        [property: JsonPropertyName("created")] DateTimeOffset Created);

    public static List<Character> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<List<Character>>(json, JsonOpts) ?? new();
        }
        catch
        {
            return new();
        }
    }

    public static bool TrySave(IEnumerable<Character> characters, out string? error)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var json = JsonSerializer.Serialize(characters, JsonOpts);
            File.WriteAllText(FilePath, json);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static List<Character> ForServer(string endpoint) =>
        Load().Where(c => string.Equals(c.ServerEndpoint, endpoint, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>
    /// Create (and derive the identity for) a new character on a server. The
    /// id is the deterministic identity hash — the SAME name on the SAME
    /// server is the same character (that determinism is what makes saves
    /// reconnect-stable on the host).
    /// </summary>
    public static Character CreateNew(string name, string endpoint)
    {
        var clean = Services.ConnectTargetService.SanitizeCharacterName(name);
        var hash8 = Services.ConnectTargetService.CharacterHash8(clean);
        var id64 = Services.ConnectTargetService.SyntheticSteamId64(clean);
        return new Character(
            Id: $"{endpoint.ToLowerInvariant()}:{hash8}",
            Name: clean,
            ServerEndpoint: endpoint,
            SteamId64: id64,
            CharHash8: hash8,
            LastPlayed: null,
            Created: DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Upsert a character into the roster (dedupe by Id) and stamp LastPlayed.
    /// Used by the connect flow so the roster reflects what's actually been
    /// played without a separate save step.
    /// </summary>
    public static void TouchPlayed(string name, string endpoint)
    {
        try
        {
            var character = CreateNew(name, endpoint);
            var all = Load();
            var idx = all.FindIndex(c => string.Equals(c.Id, character.Id, StringComparison.OrdinalIgnoreCase));
            var stamped = character with
            {
                LastPlayed = DateTimeOffset.UtcNow,
                Created = idx >= 0 ? all[idx].Created : character.Created,
            };
            if (idx >= 0) all[idx] = stamped;
            else all.Add(stamped);
            TrySave(all, out _);
        }
        catch
        {
            // Roster bookkeeping must never break a connect.
        }
    }
}
