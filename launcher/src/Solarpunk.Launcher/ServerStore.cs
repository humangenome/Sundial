using System.Text.Json;

namespace Solarpunk.Launcher;

/// <summary>
/// Persistence for the launcher's server bookmark list. Stored at
/// <c>%APPDATA%\Solarpunk\servers.json</c>.
/// </summary>
public static class ServerStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Solarpunk", "servers.json");

    public sealed record ServerRecord(
        string Label,
        string Host,
        int Port,
        int? QueryPort = null,
        string? Password = null,
        string? LastPlayerName = null,
        // Admin (RCON) password. Used to authenticate to the SolarpunkServer supervisor
        // HTTP snapshot API and to issue RCON commands. Separate from the
        // game-join Password above. Stored locally only.
        string? AdminPassword = null);

    // Tolerant of property-name case and missing fields so a JSON file
    // written by an older launcher version round-trips cleanly through a
    // newer launcher. Without PropertyNameCaseInsensitive a single
    // mismatched property name silently failed Deserialize and the
    // user's whole bookmark list got "lost" (returns new()) — which
    // then caused LoadSavedServers to drop in the bogus default
    // 127.0.0.1:27015 entry on next launch.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static List<ServerRecord> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<List<ServerRecord>>(json, JsonOpts) ?? new();
        }
        catch (Exception ex)
        {
            // Don't blow the user's list on a parse failure — back up
            // the unparseable file so they can recover their entries
            // manually instead of having them silently overwritten.
            try
            {
                if (File.Exists(FilePath))
                {
                    var backup = FilePath + ".broken-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                    File.Copy(FilePath, backup, overwrite: false);
                }
            }
            catch { /* best effort */ }
            System.Diagnostics.Trace.WriteLine($"[ServerStore] Load failed, returning empty: {ex.Message}");
            return new();
        }
    }

    public static void Save(IEnumerable<ServerRecord> records) => TrySave(records, out _);

    public static bool TrySave(IEnumerable<ServerRecord> records, out string? error)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var json = JsonSerializer.Serialize(records, JsonOpts);
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
}
