using Solarpunk.Launcher;

namespace Solarpunk.Launcher.Services;

// Surfaces the in-game UE4SS log (<Win64>\ue4ss\UE4SS.log) into Solarpunk.log so a
// support log shows whether UE4SS actually injected and the SolarpunkConnect mod
// ran — the single most useful signal when a join silently fails ("game launched
// but never connected").
//
// UE4SS writes UE4SS.log as it loads. If our proxy DLL didn't load (Defender
// quarantine, wrong proxy name, AV block), the file either won't exist or its
// mtime won't advance past launch time. We poll for ~20s after launch: if the
// log updates, we append its new tail to Solarpunk.log prefixed "[UE4SS]"; if it
// never updates, we log a clear warning so support knows the proxy didn't load.
//
// Everything here is best-effort and must never throw into the connect flow —
// the caller wraps the entry point in try/catch, and the polling runs on a
// detached background task so it doesn't delay the "Joining" status the user
// sees the moment the game process starts.
public static class Ue4ssLogTail
{
    // How long to wait for UE4SS.log to show activity after launch.
    private static readonly TimeSpan WaitWindow = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    // Cap on how much of the log tail we copy into Solarpunk.log so a huge UE4SS
    // log can't blow up the launcher log.
    private const int MaxTailBytes = 64 * 1024;
    private const int MaxTailLines = 400;

    // Kick off the (background) tail. Returns immediately; the actual polling +
    // copy runs detached so the connect flow isn't blocked for ~20s.
    public static void SurfaceAfterLaunch(string gameWin64Dir, DateTime launchAtUtc)
    {
        if (string.IsNullOrWhiteSpace(gameWin64Dir)) return;
        var logPath = Path.Combine(gameWin64Dir, "ue4ss", "UE4SS.log");
        SolarpunkLog.Debug($"[UE4SS] watching for log activity at {logPath} (window {WaitWindow.TotalSeconds:0}s)");

        // Fire-and-forget; all exceptions swallowed inside Run.
        _ = Task.Run(() => Run(logPath, launchAtUtc));
    }

    private static async Task Run(string logPath, DateTime launchAtUtc)
    {
        try
        {
            // Note where to start reading from so we only copy lines written
            // after launch. If the file already exists (a prior session), start
            // at its current length; otherwise start at 0 once it appears.
            long startOffset = 0;
            if (File.Exists(logPath))
            {
                try { startOffset = new FileInfo(logPath).Length; } catch { startOffset = 0; }
            }

            var deadline = DateTime.UtcNow + WaitWindow;
            bool sawActivity = false;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (File.Exists(logPath))
                    {
                        var info = new FileInfo(logPath);
                        // mtime advanced past launch (UTC) → UE4SS is writing.
                        if (info.LastWriteTimeUtc > launchAtUtc && info.Length >= startOffset)
                        {
                            sawActivity = true;
                            break;
                        }
                        // If the file was recreated smaller than our start offset,
                        // UE4SS rotated/recreated it — read from the top.
                        if (info.Length < startOffset)
                        {
                            startOffset = 0;
                            sawActivity = true;
                            break;
                        }
                    }
                }
                catch { /* transient IO; keep polling */ }

                await Task.Delay(PollInterval).ConfigureAwait(false);
            }

            if (!sawActivity)
            {
                SolarpunkLog.Warn("[UE4SS] log did not update after launch — UE4SS proxy likely did not load");
                return;
            }

            // Give UE4SS a beat to flush its early load lines, then copy the tail.
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            CopyTail(logPath, startOffset);
        }
        catch (Exception ex)
        {
            SolarpunkLog.Debug($"[UE4SS] log tail failed (ignored): {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void CopyTail(string logPath, long startOffset)
    {
        try
        {
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var len = fs.Length;
            var from = startOffset;
            if (from < 0 || from > len) from = 0;

            // Cap how far back we read so a very large log can't flood Solarpunk.log.
            var available = len - from;
            if (available > MaxTailBytes)
                from = len - MaxTailBytes;

            fs.Seek(from, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);
            var text = reader.ReadToEnd();
            if (string.IsNullOrWhiteSpace(text))
            {
                SolarpunkLog.Debug("[UE4SS] log had activity but no new text to copy");
                return;
            }

            var lines = text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var start = Math.Max(0, lines.Length - MaxTailLines);
            SolarpunkLog.Info($"[UE4SS] copying {lines.Length - start} line(s) from {logPath}");
            for (var i = start; i < lines.Length; i++)
                SolarpunkLog.Info($"[UE4SS] {lines[i].TrimEnd()}");
        }
        catch (Exception ex)
        {
            SolarpunkLog.Debug($"[UE4SS] could not read log tail (ignored): {ex.Message}");
        }
    }
}
