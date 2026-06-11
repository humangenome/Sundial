using System.IO.Pipes;
using System.Threading;
using Avalonia;

namespace Solarpunk.Launcher;

// Entry point for the Solarpunk client launcher.
//
// PORTED FROM BEACON (Beacon.Launcher/Program.cs), trimmed for Solarpunk:
// single-instance guard + activation pipe (so a second launch brings the
// running window to the front and can hand off a solarpunk:// link) +
// --minimized boot-at-startup support. Beacon's elevated-relaunch /
// pending-connect handshake is dropped — Solarpunk injects UE4SS via a proxy
// DLL, so it never needs to relaunch elevated.
internal static class Program
{
    public const string MinimizedArgument = "--minimized";

    // Force-enables verbose (Debug-level) Solarpunk.log output for this run,
    // independent of the .dev-build dev-mode marker. Useful for a one-off
    // diagnostic launch ("Solarpunk.exe --verbose") on a non-dev build.
    public const string VerboseArgument = "--verbose";

    // True when launched with --verbose. MainWindow ORs this with the persisted
    // setting to set SolarpunkLog.Verbose early in startup.
    public static bool VerboseRequested { get; private set; }

    // Custom URL scheme the installer registers under
    // HKCU\Software\Classes\solarpunk. Hosting panels can emit links
    // like solarpunk://add?host=X&port=Y&label=Z so "Open in Solarpunk" gets a
    // one-click server-add flow. Windows passes the full URL as argv[1].
    public const string SolarpunkUrlScheme = "solarpunk://";

    // Per-user single-instance lock, scoped to the Windows session.
    private const string SingleInstanceMutexName = @"Local\HumanGenome.Solarpunk.Launcher.SingleInstance";

    // Named pipe the running launcher listens on so a second launch can bring
    // it to the foreground (and relay a solarpunk:// link) instead of exiting
    // silently. Local\ scope matches the mutex.
    public const string ActivationPipeName = @"HumanGenome.Solarpunk.Launcher.Activate";

    // True when launched with --minimized (the installer's boot-at-startup entry).
    public static bool StartMinimized { get; private set; }

    // The raw solarpunk:// link passed on argv, if any. MainWindow consumes it on
    // startup, or it's relayed to a running launcher via the activation pipe.
    public static string? PendingSolarpunkUrl { get; private set; }

    // host:port parsed from PendingSolarpunkUrl, or null.
    public static string? PendingAddress { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        StartMinimized = args.Any(a => string.Equals(a, MinimizedArgument, StringComparison.OrdinalIgnoreCase));
        VerboseRequested = args.Any(a => string.Equals(a, VerboseArgument, StringComparison.OrdinalIgnoreCase));

        PendingSolarpunkUrl = args.FirstOrDefault(a => a is not null
            && a.StartsWith(SolarpunkUrlScheme, StringComparison.OrdinalIgnoreCase));
        PendingAddress = MainWindow.AddressFromSolarpunkUrl(PendingSolarpunkUrl);

        using var mutex = new Mutex(initiallyOwned: false, SingleInstanceMutexName, out _);
        bool acquired;
        try { acquired = mutex.WaitOne(TimeSpan.Zero, exitContext: false); }
        catch (AbandonedMutexException) { acquired = true; }

        // Another Solarpunk launcher already owns the session — poke its
        // activation pipe so it surfaces (and adds the server from the URL),
        // then exit quietly.
        if (!acquired)
        {
            TrySendActivation();
            return 0;
        }

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch { /* not held → ignore */ }
        }
    }

    // Best-effort one-shot write to the running launcher's named pipe.
    //   "activate\n"               — bring window to front (default)
    //   "url <solarpunk://...>\n"    — bring to front AND add the server
    private static void TrySendActivation()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                serverName: ".",
                pipeName: ActivationPipeName,
                direction: PipeDirection.Out);
            pipe.Connect(timeout: 1500);
            var payload = string.IsNullOrEmpty(PendingSolarpunkUrl)
                ? "activate\n"
                : $"url {PendingSolarpunkUrl}\n";
            var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();
        }
        catch
        {
            // Running launcher may have just exited, or the pipe listener
            // isn't up yet. Silent — we're exiting either way.
        }
    }

    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
