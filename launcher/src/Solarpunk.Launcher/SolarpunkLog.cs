using System.Globalization;
using System.Text;

namespace Solarpunk.Launcher;

/// <summary>
/// Verbose file logger for the launcher. Writes to
/// <c>%APPDATA%\Solarpunk\Solarpunk.log</c> — the canonical location support
/// asks customers to send when something goes wrong. Keep this central
/// because it's referenced from MainWindow + AutoUpdater + the connect
/// path; anything diagnostic worth attaching to a ticket should land here.
/// </summary>
public static class SolarpunkLog
{
    private static readonly object _gate = new();
    private static readonly string _logPath = ResolveLogPath();
    private const int MaxBytes = 5 * 1024 * 1024;     // 5 MB before truncation

    private static string ResolveLogPath()
    {
        var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(string.IsNullOrEmpty(appdata) ? "." : appdata, "Solarpunk");
        try { Directory.CreateDirectory(dir); } catch { }
        return Path.Combine(dir, "Solarpunk.log");
    }

    public static string LogPath => _logPath;

    /// <summary>
    /// When true, <see cref="Debug"/> lines are written. Default false. Set once
    /// at startup from (the .dev-build dev-mode marker || the --verbose CLI arg). Keeps
    /// non-verbose logs readable while making the full connect/join chain visible
    /// for diagnostics when something fails.
    /// </summary>
    public static bool Verbose { get; set; }

    public static void Info(string msg) => Write("INFO ", msg);
    public static void Warn(string msg) => Write("WARN ", msg);
    public static void Error(string msg) => Write("ERROR", msg);
    public static void Error(string msg, Exception ex) =>
        Write("ERROR", msg + " :: " + ex.GetType().Name + ": " + ex.Message + Environment.NewLine + ex.StackTrace);

    /// <summary>
    /// Verbose-only diagnostic line. No-op unless <see cref="Verbose"/> is true.
    /// Use for per-step detail (paths checked, files copied, probe targets) that
    /// would be noise in a normal session but is gold when a connect fails.
    /// </summary>
    public static void Debug(string msg)
    {
        if (!Verbose) return;
        Write("DEBUG", msg);
    }

    private static void Write(string level, string msg)
    {
        var line = string.Format(CultureInfo.InvariantCulture,
            "[{0:yyyy-MM-dd HH:mm:ss.fff}] {1} {2}",
            DateTime.Now, level, msg);
        lock (_gate)
        {
            try
            {
                TruncateIfTooBig();
                File.AppendAllText(_logPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Logger failures must never crash the launcher. Drop the line.
            }
        }
    }

    private static void TruncateIfTooBig()
    {
        try
        {
            var info = new FileInfo(_logPath);
            if (!info.Exists || info.Length < MaxBytes) return;
            // Keep the most recent half so the log never grows unbounded but
            // we don't wipe context the user might still need.
            var bytes = File.ReadAllBytes(_logPath);
            var tail = bytes.AsSpan(bytes.Length / 2).ToArray();
            File.WriteAllBytes(_logPath, tail);
            File.AppendAllText(_logPath, "[--- log truncated " + DateTime.Now.ToString("o", CultureInfo.InvariantCulture) + " ---]" + Environment.NewLine);
        }
        catch { }
    }
}
