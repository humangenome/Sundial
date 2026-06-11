using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Solarpunk.Launcher.Services;

namespace Solarpunk.Launcher;

public sealed class MainWindow : Window
{
    // Sundial palette — light, airy morning solarpunk. Warm cream sky
    // surfaces, warm-white paper cards, a golden-sun accent, sage-leaf
    // success green, terracotta warnings, warm charcoal ink text.
    // Deliberately distinct from Lantern (green-on-brown) and Beacon (blue/teal).
    private static readonly IBrush Cream100 = Brush("#F6EFDD");  // window base — warm cream
    private static readonly IBrush Cream50 = Brush("#FCF8EC");   // panels / console paper
    private static readonly IBrush Card = Brush("#FFFEF7");      // cards, inputs, buttons
    private static readonly IBrush Line = Brush("#E3D8BD");      // soft borders + meter tracks
    private static readonly IBrush Sun400 = Brush("#F0B23C");    // golden sun — fills/borders
    private static readonly IBrush Sun600 = Brush("#A87410");    // golden sun — text/icons on cream
    private static readonly IBrush InkOnSun = Brush("#3D2E08");  // text on golden fills
    private static readonly IBrush Sage500 = Brush("#3F8A4E");   // success / online green
    private static readonly IBrush Coral500 = Brush("#C25E26");  // warnings — warm terracotta
    private static readonly IBrush Red500 = Brush("#C04545");    // errors — readable on cream
    private static readonly IBrush Ink900 = Brush("#322E1D");    // primary text — warm charcoal
    private static readonly IBrush Ink500 = Brush("#7E7458");    // muted text
    private static readonly FontFamily ConsoleFont = new("Cascadia Mono, Consolas, Menlo, Courier New, monospace");
    private static readonly FontFamily FluentIconFont = new("Segoe Fluent Icons");
    private static readonly Uri AppIconUri = new("avares://Solarpunk.Launcher/Assets/icon.png");
    private static readonly Uri BrandLockupUri = new("avares://Solarpunk.Launcher/Assets/lockup-dark.png");
    private static readonly Lazy<WindowIcon?> SharedWindowIcon = new(LoadWindowIcon);
    private static readonly TimeSpan AutoUpdatePollInterval = TimeSpan.FromMinutes(5);
    private static readonly HttpClient ServerStatusHttp = new(new SocketsHttpHandler
    {
        // SolarpunkServer HTTP probes are direct host:port calls. Do not pay
        // Windows default-proxy/WPAD discovery on cold launcher startup.
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(1.5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = TimeSpan.FromSeconds(2),
    };
    // Real RCON commands the server implements. Anything not in this list will
    // return an "unknown rcon command" string — don't list aspirational
    // commands here.
    private static readonly string[] ConsoleCommandNames =
        ["help", "status", "players", "ping", "save snapshot", "save list"];

    private const string IconAdd = "\uE710";
    private const string IconEdit = "\uE70F";
    private const string IconRefresh = "\uE72C";
    private const string IconRemove = "\uE74D";
    private const string IconConnect = "\uE768";
    private const string IconSend = "\uE724";
    private const string IconSave = "\uE74E";
    private const string IconCancel = "\uE711";
    private const string IconClose = "\uE711";
    private const string IconSettings = "\uE713";
    private const string IconInfo = "\uE946";
    private const string IconServers = "\uE968";
    private const string IconConsole = "\uE756";
    private const string IconHosting = "\uE719";
    private const string IconExternal = "\uE8A7";
    private const string IconMinimize = "\uE921";
    private const string IconCharacters = "\uE716";
    private const string IconUse = "\uE73E";
    private const string IconMods = "\uEA86";
    private const string IconCheck = "\uE73E";
    private const string IconBlocked = "\uE733";
    private const string IconDownload = "\uE896";

    private readonly ObservableCollection<ServerEntry> _servers = new();
    private readonly List<string> _consoleHistory = new();
    private readonly LauncherSettings _settings;

    private ContentControl _contentHost = null!;
    private Button _serversNav = null!;
    private Button _consoleNav = null!;
    private Button _modsNav = null!;
    private ListBox _serverList = null!;
    private TextBlock _statusLine = null!;
    private TextBlock _heroName = null!;
    private TextBlock _heroEndpoint = null!;
    private TextBlock _heroMeta = null!;
    private TextBlock _heroPlayers = null!;
    private StackPanel _heroPlayerRoster = null!;
    private TextBlock _activityLaunch = null!;
    private ProgressBar _heroPlayerFill = null!;
    private Control _serversView = null!;
    private Control _consoleView = null!;
    private Control _modsView = null!;
    private StackPanel _modsRequiredList = null!;
    private StackPanel _modsRecommendedList = null!;
    private StackPanel _modsBlockedList = null!;
    private Border _modsRequiredSection = null!;
    private Border _modsRecommendedSection = null!;
    private Border _modsBlockedSection = null!;
    private TextBlock _modsEmptyNote = null!;
    private TextBlock _modsHeading = null!;
    private TextBlock _modsSubheading = null!;
    private TextBlock _modsStatus = null!;
    private ServerEntry? _modsLoadedFor;
    private CancellationTokenSource? _modsLoadCts;
    private TextBox _consoleHost = null!;
    private TextBox _consolePort = null!;
    private TextBox _consolePassword = null!;
    private AutoCompleteBox _consoleInput = null!;
    private Button _consoleConnect = null!;
    private StackPanel _consoleRows = null!;
    private ScrollViewer _consoleScroll = null!;

    private ServerEntry? _selected;
    private RconClient? _rcon;
    private int _consoleHistoryIndex;

    public static Styles CreateLauncherStyles() =>
        new()
        {
            new Style(s => s.OfType<Button>().Class(":pointerover"))
            {
                Setters =
                {
                    new Setter(Visual.EffectProperty, GlowEffect(0.28)),
                },
            },
            new Style(s => s.OfType<Button>().Class(":pressed"))
            {
                Setters =
                {
                    new Setter(Visual.EffectProperty, GlowEffect(0.18)),
                },
            },
            new Style(s => s.OfType<Button>().Class("link").Class(":pointerover"))
            {
                Setters =
                {
                    new Setter(Visual.EffectProperty, GlowEffect(0.34)),
                },
            },
            new Style(s => s.OfType<Button>().Class(":disabled"))
            {
                Setters =
                {
                    new Setter(Visual.EffectProperty, null),
                },
            },
            new Style(s => s.OfType<ListBoxItem>())
            {
                Setters =
                {
                    new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent),
                    new Setter(TemplatedControl.BorderBrushProperty, Brushes.Transparent),
                    new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)),
                    new Setter(TemplatedControl.PaddingProperty, new Thickness(0)),
                    new Setter(InputElement.CursorProperty, new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)),
                },
            },
            new Style(s => s.OfType<ListBoxItem>().Class(":pointerover"))
            {
                Setters =
                {
                    new Setter(TemplatedControl.BackgroundProperty, Cream50),
                    new Setter(TemplatedControl.BorderBrushProperty, Sun400),
                    new Setter(Visual.EffectProperty, GlowEffect(0.22)),
                },
            },
            new Style(s => s.OfType<ListBoxItem>().Class(":selected"))
            {
                Setters =
                {
                    new Setter(TemplatedControl.BackgroundProperty, Card),
                    new Setter(TemplatedControl.BorderBrushProperty, Sage500),
                    new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(1)),
                    new Setter(Visual.EffectProperty, GlowEffect(0.26)),
                },
            },
            new Style(s => s.OfType<TextBox>().Class(":focus"))
            {
                Setters =
                {
                    new Setter(TemplatedControl.BorderBrushProperty, Sun400),
                    new Setter(Visual.EffectProperty, GlowEffect(0.2)),
                },
            },
            new Style(s => s.OfType<AutoCompleteBox>().Class(":focus"))
            {
                Setters =
                {
                    new Setter(TemplatedControl.BorderBrushProperty, Sun400),
                    new Setter(Visual.EffectProperty, GlowEffect(0.2)),
                },
            },
        };

    private static DropShadowDirectionEffect GlowEffect(double opacity) =>
        new()
        {
            BlurRadius = 18,
            Color = Color.Parse("#E8A21B"),
            Opacity = opacity,
            ShadowDepth = 0,
        };

    private static WindowIcon? LoadWindowIcon()
    {
        try
        {
            using var stream = AssetLoader.Open(AppIconUri);
            return new WindowIcon(stream);
        }
        catch
        {
            return null;
        }
    }

    private static void ApplyWindowIcon(Window window)
    {
        var icon = SharedWindowIcon.Value;
        if (icon is not null)
            window.Icon = icon;
    }

    /// <summary>
    /// Make a modal child window chromeless — no title bar, no taskbar
    /// entry, no resize handle, no system menu. Used for Add Server,
    /// New Character, World Transfer, Settings, etc. — short-lived
    /// popups that should not pretend to be top-level windows.
    /// </summary>
    private static void ApplyChromelessDialog(Window window)
    {
        window.SystemDecorations = SystemDecorations.None;
        window.ExtendClientAreaToDecorationsHint = false;
        window.ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.NoChrome;
        window.ExtendClientAreaTitleBarHeightHint = 0;
        window.ShowInTaskbar = false;
        window.CanResize = false;

        // Light theme: a chromeless cream dialog floating over the cream main
        // window has no system frame or shadow, so without an edge it blends
        // into the content behind it. Wrap whatever Content the dialog set in
        // a 1px Line border at open time so popups read as distinct surfaces.
        window.Opened += (_, _) =>
        {
            if (window.Content is Control content && content is not Border { Tag: DialogChromeTag })
            {
                window.Content = null;
                window.Content = new Border
                {
                    Tag = DialogChromeTag,
                    BorderBrush = Line,
                    BorderThickness = new Thickness(1),
                    Child = content,
                };
            }
        };
    }

    private const string DialogChromeTag = "dialog-chrome";

    public MainWindow()
    {
        _settings = LauncherSettings.Load();

        // Dev mode (Beacon-parity): verbose logging follows the .dev-build marker
        // dropped beside the exe by scripts/dev-deploy.sh — the same signal that drives
        // the "DEV" badge below. No separate setting; --verbose force-enables on any build.
        // (Drop a .dev-build file by hand to force verbose on a customer build.)
        SolarpunkLog.Verbose = IsDevBuild() || Program.VerboseRequested;

        Title = "Sundial";
        Width = 1120;
        Height = 720;
        MinWidth = 920;
        MinHeight = 620;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Cream100;
        SystemDecorations = SystemDecorations.None;
        ExtendClientAreaToDecorationsHint = false;
        ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.NoChrome;
        ExtendClientAreaTitleBarHeightHint = 0;
        ApplyWindowIcon(this);

        Content = BuildShell();
        SetView(LauncherView.Servers);
        LoadSavedServers();
        AppendConsole("Disconnected.", ConsoleTone.Muted);

        SolarpunkLog.Info($"Launcher starting v{SolarpunkLifecycle.CurrentVersion} installDir={SolarpunkLifecycle.InstallDir ?? "(side-loaded)"} cwd={Environment.CurrentDirectory}");
        SolarpunkLog.Info($"Verbose logging {(SolarpunkLog.Verbose ? "ON" : "off")} (dev-build={IsDevBuild()}, --verbose={Program.VerboseRequested})");

        // Re-register the solarpunk:// URL scheme on every launch. The NSIS
        // installer writes this on first install, but customers who
        // updated via the auto-updater from pre-v0.3.13 never got the
        // HKCU entry. Cheap idempotent write — no admin needed.
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exe))
                    StartupRegistration.EnsureSolarpunkUrlSchemeRegistered(exe);
            }
            catch { }
        }

        // Close-to-tray: clicking the title-bar X (or pressing Alt+F4) hides
        // the window and leaves Solarpunk running in the system tray instead
        // of fully exiting. The tray icon's "Exit Solarpunk" menu sets
        // UserInitiatedExit=true via RequestRealExit() before calling
        // Close() so this handler lets the close through. Background work
        // — auto-update polling, server status polling, Solarpunk process
        // watching, activation pipe — keeps running while hidden, which is
        // the whole point: customers want Solarpunk ready to answer "is my
        // server up" without re-launching. ShutdownMode is set to
        // OnExplicitShutdown in App.cs so closing the window doesn't tear
        // down the process.
        Closing += (_, e) =>
        {
            if (UserInitiatedExit)
                return;

            // Don't fight the OS or the framework. If Windows is logging
            // out / shutting down, or Avalonia is tearing the app down
            // (Shutdown() was called explicitly), let the close happen
            // — hiding here would block logout and the OS would
            // force-terminate us anyway.
            if (e.CloseReason == WindowCloseReason.OSShutdown
                || e.CloseReason == WindowCloseReason.ApplicationShutdown)
                return;

            e.Cancel = true;
            HideToTray();
        };

        Closed += (_, _) =>
        {
            try { _windowClosedCts.Cancel(); } catch { }
            try { _connectLaunchCts?.Cancel(); } catch { }
            _rcon?.Dispose();

            // Apply a staged auto-update on exit: spawns SolarpunkUpdater.cmd
            // (written by AutoUpdater.DownloadAndStageAsync), which polls
            // for this process to release its file lock then renames
            // app.pending → app and relaunches us. No-op if no update is
            // staged or we're side-loaded (no InstallDir).
            if (_stagedUpdateVersion is not null)
            {
                var installDir = SolarpunkLifecycle.InstallDir;
                if (!string.IsNullOrEmpty(installDir))
                {
                    if (IsGameRunningForUpdateGuard())
                    {
                        SolarpunkLog.Warn("Auto-update apply skipped because Solarpunk is still running");
                    }
                    else
                    {
                        try { AutoUpdater.TriggerSwapAndExit(installDir); } catch { }
                    }
                }
            }
        };

        // Honor --minimized (used by the boot-at-startup registry entry so the
        // launcher stays out of the customer's face until they need it).
        var cli = Environment.GetCommandLineArgs();
        if (cli.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase)))
        {
            Opened += (_, _) => WindowState = WindowState.Minimized;
        }

        // solarpunk://add?host=...&port=...&label=... handed to us by Windows
        // when a customer clicks an "Open in Sundial" link on a hosting
        // provider's panel and the launcher wasn't already running. (If it was running,
        // the second instance's TrySendActivation relays the URL through the
        // activation pipe — see the pipe listener below.)
        if (!string.IsNullOrEmpty(Program.PendingSolarpunkUrl))
        {
            var url = Program.PendingSolarpunkUrl!;
            Opened += async (_, _) => await AddServerFromSolarpunkUrlAsync(url);
        }

        // Background updater. Fully auto: poll on launch, re-poll every
        // few minutes, auto-download + stage when a new version
        // appears. SolarpunkUpdater.cmd swaps it in on next exit so the update
        // is live the next time the customer opens the launcher. Customer
        // never has to click anything.
        if (_settings.CheckForUpdatesOnStart)
        {
            Opened += async (_, _) => await CheckAndStageUpdateAsync();
            _autoUpdatePoll = new DispatcherTimer { Interval = AutoUpdatePollInterval };
            _autoUpdatePoll.Tick += async (_, _) => await CheckAndStageUpdateAsync();
            _autoUpdatePoll.Start();
            Closed += (_, _) => _autoUpdatePoll?.Stop();
        }

        // Periodic refreshes. The whole list refreshes slowly so offline
        // entries can recover. The selected server refreshes faster when it
        // is online so the hero player count/roster stays live.
        _serverListPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _serverListPoll.Tick += async (_, _) =>
        {
            try { await RefreshAllAsync(silent: true); } catch { }
        };
        _selectedServerPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _selectedServerPoll.Tick += async (_, _) =>
        {
            try { await RefreshSelectedAsync(); } catch { }
        };
        // On window open: kick a refresh immediately so the cards leave their
        // "Checking" placeholder as soon as the launcher is visible.
        Opened += async (_, _) =>
        {
            _serverListPoll.Start();
            _selectedServerPoll.Start();
            try { await RefreshAllAsync(); } catch { }
        };
        Closed += (_, _) =>
        {
            _serverListPoll.Stop();
            _selectedServerPoll.Stop();
        };

        // Solarpunk process watcher. Polls every 2 seconds for a running
        // SolarpunkSteam-Win64-Shipping.exe. When the game is up we disable Connect
        // (the launcher's job is already done — pressing Connect twice spawns
        // a second game over the running one) and surface a "Solarpunk is
        // running" banner on the hero so the user understands why.
        _gameProcessPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _gameProcessPoll.Tick += (_, _) => RefreshGameRunningState();
        Opened += (_, _) =>
        {
            _gameProcessPoll.Start();
            RefreshGameRunningState();
        };
        Closed += (_, _) => _gameProcessPoll.Stop();

        // Activation pipe — a second launcher invocation (Start Menu while
        // we're already running) connects to this pipe and writes
        // "activate\n" instead of opening its own window. We bring our
        // window to the foreground so the customer doesn't think the icon
        // is broken. See Program.TrySendActivation for the client side.
        _activationPipeCts = new CancellationTokenSource();
        _ = StartActivationPipeListener(_activationPipeCts.Token);
        Closed += (_, _) =>
        {
            try { _activationPipeCts.Cancel(); } catch { }
        };
    }

    private DispatcherTimer? _autoUpdatePoll;
    private DispatcherTimer? _serverListPoll;
    private DispatcherTimer? _selectedServerPoll;
    private DispatcherTimer? _gameProcessPoll;
    private int _refreshAllInFlight;
    private int _refreshSelectedInFlight;
    private bool _gameRunning;
    private Button? _connectButton;
    private Button? _closeGameButton;

    // Character-name box in the hero connect row. The name is the player's
    // save identity on the host (the ?Name= login option the server-side
    // InitNewPlayer hook keys synthetic SteamID64s from), so Connect requires
    // it. Persisted per server (ServerEntry.LastPlayerName) + rostered in
    // CharacterStore so switching servers restores the right character.
    private TextBox? _characterNameBox;
    private readonly CancellationTokenSource _windowClosedCts = new();
    private CancellationTokenSource? _connectLaunchCts;
    // Guards the connect path against re-entry. The Connect handler runs the
    // file/process-heavy ConnectFlow off the UI thread; a second click while
    // that is in flight is a no-op.
    private bool _connectInFlight;

    // Tray-exit gate. UserInitiatedExit is true when the tray menu's
    // "Exit Solarpunk" item (or anywhere else that legitimately wants to tear
    // down the process — staged-update apply) calls RequestRealExit().
    // Without it, Closing redirects to HideToTray().
    public bool UserInitiatedExit { get; private set; }

    /// <summary>
    /// Hide the window from the taskbar + screen. The process keeps
    /// running so polling timers (game watcher, auto-update poll, server
    /// status refresh) and the activation pipe listener stay live.
    /// Customer re-opens via the tray icon's "Show Solarpunk" menu item or
    /// by double-clicking the tray icon, both of which call
    /// ShowFromTray().
    /// </summary>
    public void HideToTray()
    {
        try
        {
            ShowInTaskbar = false;
            Hide();
            SolarpunkLog.Info("Minimized to tray");
        }
        catch (Exception ex)
        {
            SolarpunkLog.Warn($"HideToTray failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Restore the window from a hidden (tray) state. Also handles the
    /// case where the window was just minimized (existing
    /// activation-pipe behavior), so both code paths can share one
    /// entry point.
    /// </summary>
    public void ShowFromTray()
    {
        try
        {
            if (!IsVisible)
            {
                ShowInTaskbar = true;
                Show();
            }
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
        }
        catch (Exception ex)
        {
            SolarpunkLog.Warn($"ShowFromTray failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Real exit: sets UserInitiatedExit so the Closing handler lets the
    /// window close instead of redirecting to the tray, then asks the
    /// Avalonia desktop lifetime to shut the process down. App.cs runs
    /// the launcher with ShutdownMode.OnExplicitShutdown so the close
    /// alone wouldn't end the process — the tray icon's host stays
    /// alive — and SolarpunkUpdater.cmd's tasklist :waitloop would then
    /// hang forever waiting for Solarpunk.Launcher.exe to exit. Calling
    /// Shutdown() here ensures the whole process terminates so the
    /// staged auto-update can swap app.pending -> app and relaunch.
    /// </summary>
    public void RequestRealExit()
    {
        UserInitiatedExit = true;
        try { Close(); } catch { }
        try
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown(0);
        }
        catch { }
    }
    /// <summary>
    /// Named-pipe listener that pairs with <see cref="Program.TrySendActivation"/>.
    /// When a second launcher invocation hits our single-instance mutex,
    /// it writes "activate\n" to this pipe; we bring our own window to
    /// the foreground so the customer's second click on the Start Menu
    /// icon doesn't look like the launcher is broken.
    /// </summary>
    private async Task StartActivationPipeListener(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe = new System.IO.Pipes.NamedPipeServerStream(
                    Program.ActivationPipeName,
                    System.IO.Pipes.PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    System.IO.Pipes.PipeTransmissionMode.Byte,
                    System.IO.Pipes.PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                // The client may send one of:
                //   "activate\n"             — bring window to front
                //   "url <solarpunk://...>\n"   — bring to front + add server
                // Use a large enough buffer to capture a full URL (~2KB
                // is generous; solarpunk:// URLs are typically ~150 bytes).
                var buf = new byte[4096];
                var read = await pipe.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false);
                var payload = read > 0
                    ? System.Text.Encoding.UTF8.GetString(buf, 0, read).Trim()
                    : "";

                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    ShowFromTray();
                    if (payload.StartsWith("url ", StringComparison.Ordinal))
                    {
                        var url = payload.Substring(4).Trim();
                        if (url.StartsWith(Program.SolarpunkUrlScheme, StringComparison.OrdinalIgnoreCase))
                            await AddServerFromSolarpunkUrlAsync(url);
                    }
                });
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // Pipe glitch — back off briefly and try again. This is
                // a best-effort UX nicety, never block the launcher.
                try { await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
                catch { return; }
            }
        }
    }

    private string? _stagedUpdateVersion;

    // Gate concurrent staging — the background poll, the update timer, and
    // Settings → "Check for updates" can all race onto app.pending/ +
    // update.zip simultaneously. The timer firing while a poll is
    // still downloading produced overlapping zip extracts that corrupted
    // app.pending. Hold a 1-permit semaphore around every staging path.
    private readonly SemaphoreSlim _stagingGate = new(1, 1);

    private CancellationTokenSource? _activationPipeCts;

    /// <summary>
    /// Outcome of a single staging attempt. Settings → "Check for updates"
    /// uses this to render its inline status text. The background poll
    /// path only reads it to decide what to render on _statusLine.
    /// </summary>
    internal sealed record StageOutcome(
        bool UpdateAvailable,
        bool Staged,
        string? Version,
        string? Error,
        bool SideLoadedNoAutoApply);

    /// <summary>
    /// Single staging code path — used by both the background poll and the
    /// Settings dialog. Serialized via <see cref="_stagingGate"/> so the
    /// update timer + on-launch poll + manual click can't fight over
    /// app.pending/ + update.zip.
    /// </summary>
    internal async Task<StageOutcome> StageUpdateOnceAsync()
    {
        await _stagingGate.WaitAsync();
        try
        {
            var current = SolarpunkLifecycle.CurrentVersion;
            var result = await AutoUpdater.CheckAsync(current);
            if (result.Error is not null)
                return new StageOutcome(false, false, null, result.Error, false);
            if (!result.UpdateAvailable || result.Manifest is null)
                return new StageOutcome(false, false, null, null, false);

            var version = result.Manifest.Version;
            if (string.Equals(_stagedUpdateVersion, version, StringComparison.Ordinal))
            {
                // Already staged this version — surface as "ready" without
                // re-downloading. Lets Settings show consistent state if
                // the customer clicked Check for updates right after the
                // background poll fired.
                return new StageOutcome(true, true, version, null, false);
            }

            var installDir = SolarpunkLifecycle.InstallDir;
            if (string.IsNullOrWhiteSpace(installDir))
            {
                // Side-loaded build — can't auto-apply, surface a hint only.
                return new StageOutcome(true, false, version, null, true);
            }

            var download = await AutoUpdater.DownloadAndStageAsync(result.Manifest, installDir);
            if (!download.Success)
                return new StageOutcome(true, false, version, download.Error, false);

            _stagedUpdateVersion = version;
            return new StageOutcome(true, true, version, null, false);
        }
        finally
        {
            _stagingGate.Release();
        }
    }

    /// <summary>
    /// Background poll path: stages whatever's offered, then auto-closes the
    /// window after a 5s grace so SolarpunkUpdater.cmd applies on exit.
    /// </summary>
    private async Task CheckAndStageUpdateAsync()
    {
        try
        {
            var outcome = await StageUpdateOnceAsync();

            if (!outcome.UpdateAvailable) return;

            if (outcome.SideLoadedNoAutoApply)
            {
                _statusLine.Text = $"Update available: {outcome.Version}";
                _activityLaunch.Text = $"v{outcome.Version} available — install via the bundled installer to enable auto-update.";
                return;
            }
            if (!outcome.Staged)
            {
                _statusLine.Text = "Update download failed";
                _activityLaunch.Text = $"Could not download {outcome.Version}: {outcome.Error}";
                return;
            }

            _statusLine.Text = $"Restarting Solarpunk to apply v{outcome.Version}";
            _activityLaunch.Text = $"Restarting in 2s to apply v{outcome.Version}";

            if (IsGameRunningForUpdateGuard())
            {
                _statusLine.Text = $"Update ready: v{outcome.Version}";
                _activityLaunch.Text = "Quit Solarpunk to finish the Solarpunk update.";
                return;
            }

            // Auto-apply: tiny grace period (2s) so the user can see what's
            // happening, then close the window ourselves. The Closed handler
            // triggers SolarpunkUpdater.cmd which renames app.pending → app and
            // relaunches Solarpunk.Launcher.exe with the install root as CWD.
            var version = outcome.Version!;
            await Task.Delay(TimeSpan.FromSeconds(2));
            if (string.Equals(_stagedUpdateVersion, version, StringComparison.Ordinal))
            {
                Dispatcher.UIThread.Post(() => RequestRealExit());
            }
        }
        catch
        {
            // Silent — next poll picks it up.
        }
    }

    // Dev mode (Beacon-parity): a .dev-build marker beside the exe (dropped by
    // scripts/dev-deploy.sh) means this is a developer/iteration build. It drives
    // both the amber "DEV" badge and verbose logging — there is no separate setting.
    private static bool IsDevBuild()
    {
        try { return File.Exists(Path.Combine(AppContext.BaseDirectory, ".dev-build")); }
        catch { return false; }
    }

    private Control BuildShell()
    {
        var shell = new Grid
        {
            RowDefinitions = new RowDefinitions("48,*,34"),
        };

        var titleBar = BuildTitleBar();
        Grid.SetRow(titleBar, 0);
        shell.Children.Add(titleBar);

        _contentHost = new ContentControl
        {
            Margin = new Thickness(18, 16, 18, 14),
        };
        Grid.SetRow(_contentHost, 1);
        shell.Children.Add(_contentHost);

        var statusBar = BuildStatusBar();
        Grid.SetRow(statusBar, 2);
        shell.Children.Add(statusBar);

        return new Border
        {
            Background = Cream100,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            Child = shell,
        };
    }

    // Hosting CTA — opens the SurvivalServers Solarpunk page with launcher UTM
    // attribution. Used by the title-bar "Rent a Server" button and any other
    // hosting links in the app so analytics can attribute conversions.
    private const string HostingUrl =
        "https://www.survivalservers.com/services/game_servers/solarpunk/?utm_source=solarpunk_launcher&utm_medium=app&utm_campaign=rent_a_server";

    // SurvivalServers homepage link used in the About dialog. Same UTM
    // attribution scheme.
    private const string SurvivalServersUrl =
        "https://www.survivalservers.com/?utm_source=solarpunk_launcher&utm_medium=app&utm_campaign=about";

    private void OpenUrlWithStatus(string url)
    {
        if (TryOpenUrl(url, out var error))
            return;

        _statusLine.Text = "Could not open browser";
        _activityLaunch.Text = $"Could not open link: {error}";
    }

    private static void OpenUrlWithDialogStatus(string url, TextBlock errorLine)
    {
        if (TryOpenUrl(url, out var error))
        {
            errorLine.Text = "";
            return;
        }

        errorLine.Text = $"Could not open link: {error}";
    }

    private static bool TryOpenUrl(string url, out string? error)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private Control BuildTitleBar()
    {
        var titleBar = new Grid
        {
            // Col layout: brand | Servers | Console | Mods | spacer | RentAServer | Settings | About | min | close
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,*,Auto,Auto,Auto,Auto,Auto"),
            Background = Cream100,
            Height = 48,
        };
        titleBar.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;

            if (e.Source is Button || e.Source is Control source && source.FindAncestorOfType<Button>() is not null)
                return;

            BeginMoveDrag(e);
        };

        // Brand: single lockup image (sundial mark + SUNDIAL wordmark) — both
        // the icon and the wordmark live inside this PNG, so no separate icon
        // or TextBlock here. Custom chrome means there's no OS title bar to
        // host the window icon, so we render the brand ourselves.
        var brandStack = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 28, 0),
        };

        try
        {
            using var lockupStream = AssetLoader.Open(BrandLockupUri);
            var lockupImage = new Image
            {
                Source = new Avalonia.Media.Imaging.Bitmap(lockupStream),
                Height = 40,
                VerticalAlignment = VerticalAlignment.Center,
                Stretch = Avalonia.Media.Stretch.Uniform,
            };
            brandStack.Children.Add(lockupImage);
        }
        catch
        {
            // Asset missing — fall back to the plain wordmark so the title
            // bar never renders empty.
            brandStack.Children.Add(new TextBlock
            {
                Text = "Sundial",
                FontSize = 18,
                FontWeight = FontWeight.SemiBold,
                Foreground = Ink900,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        Grid.SetColumn(brandStack, 0);
        titleBar.Children.Add(brandStack);

        _serversNav = BuildNavButton(IconServers, "Servers");
        _serversNav.Click += (_, _) => SetView(LauncherView.Servers);
        Grid.SetColumn(_serversNav, 1);
        titleBar.Children.Add(_serversNav);

        _consoleNav = BuildNavButton(IconConsole, "Console");
        _consoleNav.Click += (_, _) => SetView(LauncherView.Console);
        Grid.SetColumn(_consoleNav, 2);
        titleBar.Children.Add(_consoleNav);

        _modsNav = BuildNavButton(IconMods, "Mods");
        _modsNav.Click += (_, _) => SetView(LauncherView.Mods);
        Grid.SetColumn(_modsNav, 3);
        titleBar.Children.Add(_modsNav);

        // Rent-a-Server CTA — outlined in the golden-sun accent palette
        // (not a loud filled button). Visible but doesn't yell at the user.
        var rentButton = new Button
        {
            Content = ButtonContent(IconHosting, "Rent a Server"),
            Background = Card,
            Foreground = Sun600,
            BorderBrush = Sun400,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 6),
            Margin = new Thickness(0, 8, 12, 8),
            FontWeight = FontWeight.Medium,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        rentButton.Click += (_, _) => OpenUrlWithStatus(HostingUrl);
        Grid.SetColumn(rentButton, 5);
        titleBar.Children.Add(rentButton);

        var settings = BuildNavButton(IconSettings, "Settings");
        settings.Click += async (_, _) => await ShowSettingsDialogAsync();
        Grid.SetColumn(settings, 6);
        titleBar.Children.Add(settings);

        var about = BuildNavButton(IconInfo, "About");
        about.Click += async (_, _) => await ShowAboutDialogAsync();
        Grid.SetColumn(about, 7);
        titleBar.Children.Add(about);

        var minimize = BuildWindowButton(IconMinimize, "Minimize");
        minimize.Click += (_, _) => WindowState = WindowState.Minimized;
        Grid.SetColumn(minimize, 8);
        titleBar.Children.Add(minimize);

        var close = BuildWindowButton(IconClose, "Close");
        // Plain text tint — the X is a close-to-tray button, not a
        // destructive action. Red made it read as a warning state.
        close.Foreground = Ink900;
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 9);
        titleBar.Children.Add(close);

        return new Border
        {
            BorderBrush = Line,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = titleBar,
        };
    }

    private Control BuildStatusBar()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Background = Cream100,
        };

        _statusLine = new TextBlock
        {
            Text = "Ready",
            Foreground = Ink500,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(18, 0, 12, 0),
        };
        _activityLaunch = _statusLine;
        Grid.SetColumn(_statusLine, 0);
        grid.Children.Add(_statusLine);

        // Bottom-right version line + DEV badge.
        // Side-loaded dev builds drop a .dev-build marker next to the exe
        // (scripts/dev-deploy.sh). When present, render an amber "DEV"
        // pill so the iteration build is unambiguously distinct from a
        // tagged install. Tagged installs show plain "vX.Y.Z".
        var isDev = IsDevBuild();

        var versionRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 18, 0),
            Spacing = 8,
        };
        if (isDev)
        {
            var devBadge = new Border
            {
                Background = Coral500,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 1),
                Child = new TextBlock
                {
                    Text = "DEV",
                    Foreground = Brush("#FFF6E3"),
                    FontSize = 10,
                    FontWeight = FontWeight.Bold,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                VerticalAlignment = VerticalAlignment.Center,
            };
            versionRow.Children.Add(devBadge);
        }
        var version = new TextBlock
        {
            Text = "v" + SolarpunkLifecycle.CurrentVersion,
            Foreground = Ink500,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        versionRow.Children.Add(version);
        Grid.SetColumn(versionRow, 1);
        grid.Children.Add(versionRow);

        return new Border
        {
            BorderBrush = Line,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = grid,
        };
    }

    private Control BuildServersView()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("420,*"),
        };

        var serverDeck = BuildServerDeck();
        Grid.SetColumn(serverDeck, 0);
        grid.Children.Add(serverDeck);

        var hero = BuildHero();
        hero.Margin = new Thickness(16, 0, 0, 0);
        Grid.SetColumn(hero, 1);
        grid.Children.Add(hero);

        return grid;
    }

    private Control BuildServerDeck()
    {
        var layout = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Margin = new Thickness(16),
        };

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto"),
        };

        var title = new TextBlock
        {
            Text = "Server list",
            Foreground = Ink900,
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(title, 0);
        header.Children.Add(title);

        var add = BuildIconToolButton(IconAdd, "Add server");
        add.Click += async (_, _) => await ShowAddServerDialogAsync();
        Grid.SetColumn(add, 1);
        header.Children.Add(add);

        var edit = BuildIconToolButton(IconEdit, "Edit selected server");
        edit.Click += async (_, _) => await ShowEditServerDialogAsync();
        Grid.SetColumn(edit, 2);
        header.Children.Add(edit);

        var refresh = BuildIconToolButton(IconRefresh, "Refresh servers");
        refresh.Click += (_, _) => _ = RefreshAllAsync();
        Grid.SetColumn(refresh, 3);
        header.Children.Add(refresh);

        var remove = BuildIconToolButton(IconRemove, "Remove selected server");
        remove.Click += (_, _) => RemoveSelected();
        Grid.SetColumn(remove, 4);
        header.Children.Add(remove);

        Grid.SetRow(header, 0);
        layout.Children.Add(header);

        _serverList = new ListBox
        {
            ItemsSource = _servers,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 12, 0, 0),
            ItemTemplate = new FuncDataTemplate<ServerEntry>((server, _) => BuildServerCard(server), true),
        };
        _serverList.SelectionChanged += (_, _) => SelectServer(_serverList.SelectedItem as ServerEntry);

        Grid.SetRow(_serverList, 1);
        layout.Children.Add(_serverList);

        return Panel(layout);
    }

    private Control BuildServerCard(ServerEntry? server)
    {
        if (server is null)
            return new Border();

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("58,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"),
        };

        // Left-column status indicator. Beacon used an animated SonarIndicator
        // here; Solarpunk uses a plain color dot (Ember/orange = checking, Leaf/green = online,
        // Amber = online-but-outdated, Red = offline) bound to SignalSummary —
        // same palette, no animation timer.
        var statusDisc = new Avalonia.Controls.Shapes.Ellipse
        {
            Width = 18,
            Height = 18,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        };
        statusDisc.Bind(Avalonia.Controls.Shapes.Shape.FillProperty, new Binding(nameof(ServerEntry.SignalSummary))
        {
            Converter = SignalBrushConverter.Instance,
        });
        ToolTip.SetTip(statusDisc, new Binding(nameof(ServerEntry.SignalSummary)));
        Grid.SetColumn(statusDisc, 0);
        Grid.SetRowSpan(statusDisc, 4);
        grid.Children.Add(statusDisc);

        var titleRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
        };

        var name = new TextBlock
        {
            Foreground = Ink900,
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        name.Bind(TextBlock.TextProperty, new Binding(nameof(ServerEntry.DisplayName)));
        Grid.SetColumn(name, 0);
        titleRow.Children.Add(name);

        // Status dot. Color-only — Ember/orange = checking, Leaf/green = online,
        // Amber = online-but-outdated, Red = offline. No text; vibes over
        // labels for the nature/solarpunk aesthetic.
        var status = new Avalonia.Controls.Shapes.Ellipse
        {
            Width = 10,
            Height = 10,
            Margin = new Thickness(10, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        status.Bind(Avalonia.Controls.Shapes.Shape.FillProperty, new Binding(nameof(ServerEntry.SignalSummary))
        {
            Converter = SignalBrushConverter.Instance,
        });
        ToolTip.SetTip(status, new Binding(nameof(ServerEntry.SignalSummary)));
        Grid.SetColumn(status, 1);
        titleRow.Children.Add(status);

        Grid.SetColumn(titleRow, 1);
        Grid.SetRow(titleRow, 0);
        grid.Children.Add(titleRow);

        var endpoint = new TextBlock
        {
            Foreground = Ink500,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        endpoint.Bind(TextBlock.TextProperty, new Binding(nameof(ServerEntry.Endpoint)));
        Grid.SetColumn(endpoint, 1);
        Grid.SetRow(endpoint, 1);
        grid.Children.Add(endpoint);

        var meta = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 8, 0, 0),
        };
        var map = new TextBlock
        {
            Foreground = Ink500,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        map.Bind(TextBlock.TextProperty, new Binding(nameof(ServerEntry.Map)));
        Grid.SetColumn(map, 0);
        meta.Children.Add(map);

        var latency = new TextBlock
        {
            Foreground = Sun600,
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            Margin = new Thickness(10, 0, 0, 0),
        };
        latency.Bind(TextBlock.TextProperty, new Binding(nameof(ServerEntry.LatencySummary)));
        Grid.SetColumn(latency, 1);
        meta.Children.Add(latency);
        Grid.SetColumn(meta, 1);
        Grid.SetRow(meta, 2);
        grid.Children.Add(meta);

        var players = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 8, 0, 0),
        };
        var fill = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Height = 6,
            Background = Line,
            Foreground = Sage500,
            VerticalAlignment = VerticalAlignment.Center,
        };
        fill.Bind(RangeBase.ValueProperty, new Binding(nameof(ServerEntry.PlayerFill)));
        Grid.SetColumn(fill, 0);
        players.Children.Add(fill);

        var summary = new TextBlock
        {
            Foreground = Ink500,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        summary.Bind(TextBlock.TextProperty, new Binding(nameof(ServerEntry.PlayerSummary)));
        Grid.SetColumn(summary, 1);
        players.Children.Add(summary);

        Grid.SetColumn(players, 1);
        Grid.SetRow(players, 3);
        grid.Children.Add(players);

        return new Border
        {
            Background = Card,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 10),
            Child = grid,
        };
    }

    private Control BuildHero()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*"),
            Margin = new Thickness(22),
        };

        var content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,8,Auto,Auto,*"),
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        _heroName = new TextBlock
        {
            Text = "No server selected",
            Foreground = Ink900,
            FontSize = 30,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetRow(_heroName, 0);
        content.Children.Add(_heroName);

        _heroEndpoint = new TextBlock
        {
            Text = "Select a server from the list",
            Foreground = Ink500,
            FontSize = 14,
        };
        Grid.SetRow(_heroEndpoint, 1);
        content.Children.Add(_heroEndpoint);

        _heroMeta = new TextBlock
        {
            Text = "",
            Foreground = Ink500,
            FontSize = 14,
            Margin = new Thickness(0, 4, 0, 0),
        };
        Grid.SetRow(_heroMeta, 2);
        content.Children.Add(_heroMeta);

        var meter = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
        };
        _heroPlayerFill = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Height = 8,
            Background = Line,
            Foreground = Sage500,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(_heroPlayerFill, 0);
        meter.Children.Add(_heroPlayerFill);

        _heroPlayers = new TextBlock
        {
            Text = "No data",
            Foreground = Ink500,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };
        Grid.SetColumn(_heroPlayers, 1);
        meter.Children.Add(_heroPlayers);
        Grid.SetRow(meter, 4);
        content.Children.Add(meter);

        // Per-player roster — names + connection duration as reported by
        // /api/v1/players. The supervisor must be enumerating the game's PlayerController
        // list and shipping PlayerListSnapshot frames for this to populate;
        // empty until that lands or no players are connected.
        _heroPlayerRoster = new StackPanel
        {
            Spacing = 2,
            Margin = new Thickness(0, 6, 0, 0),
        };
        Grid.SetRow(_heroPlayerRoster, 5);
        content.Children.Add(_heroPlayerRoster);

        // Character + World + connect row. The character name is the join
        // identity (?Name= → synthetic SteamID64 on the host → per-character
        // save shard), so it sits right next to Connect. The picker button
        // opens the per-server character roster (CharacterStore).
        var launch = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"),
            VerticalAlignment = VerticalAlignment.Bottom,
        };

        var characterRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var characterBox = BuildTextBox("Character name");
        characterBox.MaxWidth = 260;
        characterBox.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(characterBox, 0);
        characterRow.Children.Add(characterBox);
        _characterNameBox = characterBox;

        var characterPick = BuildIconToolButton(IconCharacters, "Characters");
        characterPick.Margin = new Thickness(6, 0, 0, 0);
        characterPick.Click += async (_, _) => await ShowCharacterPickerAsync();
        Grid.SetColumn(characterPick, 1);
        characterRow.Children.Add(characterPick);

        Grid.SetColumn(characterRow, 0);
        launch.Children.Add(characterRow);

        var worldBtn = BuildToolButton("World");
        worldBtn.Margin = new Thickness(10, 0, 0, 0);
        worldBtn.Click += async (_, _) => await ShowWorldTransferDialogAsync();
        Grid.SetColumn(worldBtn, 1);
        launch.Children.Add(worldBtn);

        var connect = BuildPrimaryButton("Connect");
        connect.MinWidth = 150;
        connect.Margin = new Thickness(10, 0, 0, 0);
        connect.Click += async (_, _) => await ConnectAsync();
        Grid.SetColumn(connect, 2);
        launch.Children.Add(connect);
        // Remember the button so the Solarpunk process watcher can flip its
        // IsEnabled / label when Solarpunk is already running. Without this,
        // a customer who Alt-Tab'd back to Solarpunk while Solarpunk was up
        // could click Connect a second time and spawn a duplicate game
        // process on top of the running one.
        _connectButton = connect;

        // Close Solarpunk button. Shown only when _gameRunning is true;
        // lets the customer force-quit the game from the launcher without
        // alt-tabbing back to a stuck game window. Hidden in steady state.
        // Muted-red palette so it reads as "destructive but not loud."
        // Avalonia's default Button template applies :pointerover and
        // :pressed pseudo-classes that override Background — without
        // explicit Styles entries, hover snaps back to the FluentTheme
        // accent (cyan in our case) and looks like a Connect button.
        var closeGame = new Button
        {
            Content = ButtonContent(IconCancel, "Close Solarpunk"),
            MinWidth = 170,
            MinHeight = 34,
            Padding = new Thickness(12, 7),
            Margin = new Thickness(10, 0, 0, 0),
            IsVisible = false,
            Background = Brush("#FAE9E4"),
            BorderBrush = Brush("#D9A399"),
            Foreground = Brush("#9C3B2E"),
            BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            FontWeight = FontWeight.SemiBold,
        };
        // Hover + pressed overrides — slightly deeper rose on hover, deeper
        // again on click, so the button still reads as destructive through
        // every interaction state on the light theme.
        var hoverStyle = new Avalonia.Styling.Style(s => s.OfType<Button>().Class(":pointerover").Template().OfType<Avalonia.Controls.Presenters.ContentPresenter>());
        hoverStyle.Setters.Add(new Avalonia.Styling.Setter(Avalonia.Controls.Presenters.ContentPresenter.BackgroundProperty, Brush("#F6DCD4")));
        hoverStyle.Setters.Add(new Avalonia.Styling.Setter(Avalonia.Controls.Presenters.ContentPresenter.BorderBrushProperty, Brush("#C9897C")));
        hoverStyle.Setters.Add(new Avalonia.Styling.Setter(Avalonia.Controls.Presenters.ContentPresenter.ForegroundProperty, Brush("#8B2F23")));
        var pressedStyle = new Avalonia.Styling.Style(s => s.OfType<Button>().Class(":pressed").Template().OfType<Avalonia.Controls.Presenters.ContentPresenter>());
        pressedStyle.Setters.Add(new Avalonia.Styling.Setter(Avalonia.Controls.Presenters.ContentPresenter.BackgroundProperty, Brush("#F0CCC1")));
        pressedStyle.Setters.Add(new Avalonia.Styling.Setter(Avalonia.Controls.Presenters.ContentPresenter.BorderBrushProperty, Brush("#B7715F")));
        pressedStyle.Setters.Add(new Avalonia.Styling.Setter(Avalonia.Controls.Presenters.ContentPresenter.ForegroundProperty, Brush("#7A281C")));
        closeGame.Styles.Add(hoverStyle);
        closeGame.Styles.Add(pressedStyle);
        closeGame.Click += async (_, _) => await CloseRunningGameAsync();
        Grid.SetColumn(closeGame, 2);
        launch.Children.Add(closeGame);
        _closeGameButton = closeGame;
        Grid.SetRow(launch, 6);
        content.Children.Add(launch);

        Grid.SetColumn(content, 0);
        grid.Children.Add(content);

        return Panel(grid);
    }

    private Control BuildConsoleView()
    {
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
        };

        // Single-row connection bar: Host / Port / Admin password (RCON)
        // / Connect. MinWidth 920 leaves plenty of room. Earlier this was
        // a two-row layout because of a narrow-width concern that no
        // longer applies — the two-row variant left a confusing blank
        // strip at the top of the Console tab in some renders, and the
        // Admin password field disappeared visually on some sizes.
        var connection = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,90,Auto,*,Auto"),
            Margin = new Thickness(12),
        };

        connection.Children.Add(FormLabel("Host", 0));
        _consoleHost = BuildTextBox("127.0.0.1");
        _consoleHost.Text = "127.0.0.1";
        _consoleHost.Margin = new Thickness(6, 0, 12, 0);
        Grid.SetColumn(_consoleHost, 1);
        connection.Children.Add(_consoleHost);

        connection.Children.Add(FormLabel("Port", 2));
        _consolePort = BuildTextBox("");
        _consolePort.Margin = new Thickness(6, 0, 12, 0);
        Grid.SetColumn(_consolePort, 3);
        connection.Children.Add(_consolePort);

        var pwLabel = FormLabel("Admin password", 4);
        connection.Children.Add(pwLabel);
        _consolePassword = BuildTextBox("");
        // Show password in plaintext. The Console tab is an admin-only
        // RCON workbench, not a user sign-in, and the user just typed
        // what they want to verify.
        _consolePassword.Margin = new Thickness(6, 0, 12, 0);
        Grid.SetColumn(_consolePassword, 5);
        connection.Children.Add(_consolePassword);

        _consoleConnect = BuildPrimaryButton("Connect");
        _consoleConnect.Click += async (_, _) => await ConsoleConnectAsync();
        Grid.SetColumn(_consoleConnect, 6);
        connection.Children.Add(_consoleConnect);

        Grid.SetRow(connection, 0);
        grid.Children.Add(Panel(connection));

        _consoleRows = new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(12),
        };
        _consoleScroll = new ScrollViewer
        {
            Content = _consoleRows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var terminal = new Border
        {
            Background = Cream50,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = _consoleScroll,
        };
        Grid.SetRow(terminal, 1);
        terminal.Margin = new Thickness(0, 12, 0, 0);
        grid.Children.Add(terminal);

        var input = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(12),
        };
        var prompt = new TextBlock
        {
            Text = ">",
            Foreground = Sun600,
            FontFamily = ConsoleFont,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(prompt, 0);
        input.Children.Add(prompt);

        _consoleInput = BuildCommandBox();
        _consoleInput.KeyDown += async (_, e) => await HandleConsoleInputKeyAsync(e);
        Grid.SetColumn(_consoleInput, 1);
        input.Children.Add(_consoleInput);

        var send = BuildToolButton("Send");
        send.Margin = new Thickness(8, 0, 0, 0);
        send.Click += async (_, _) => await ConsoleSendAsync();
        Grid.SetColumn(send, 2);
        input.Children.Add(send);
        Grid.SetRow(input, 2);
        var inputPanel = Panel(input);
        inputPanel.Margin = new Thickness(0, 12, 0, 0);
        grid.Children.Add(inputPanel);

        return grid;
    }

    private TextBlock FormLabel(string text, int column)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = Ink500,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(label, column);
        return label;
    }

    // ------------ Mods tab ------------

    // Read-only surface over the selected server's mod manifest. The
    // launcher already auto-installs required mods on Connect (see
    // EnsureManifestModsInstalledAsync / ModInstallService); this view
    // SURFACES that state — required + recommended mods with their local
    // install status, plus any mods the server has blocked. It does not
    // download or install anything itself.
    private Control BuildModsView()
    {
        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Margin = new Thickness(16),
        };

        // Header row: title/subtitle on the left, status + Refresh on the right.
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
        };

        var titleStack = new StackPanel { Spacing = 2 };
        _modsHeading = new TextBlock
        {
            Text = "Mods",
            Foreground = Ink900,
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
        };
        _modsSubheading = new TextBlock
        {
            Text = "Select a server to see its mods",
            Foreground = Ink500,
            FontSize = 13,
        };
        titleStack.Children.Add(_modsHeading);
        titleStack.Children.Add(_modsSubheading);
        Grid.SetColumn(titleStack, 0);
        header.Children.Add(titleStack);

        _modsStatus = new TextBlock
        {
            Text = "",
            Foreground = Ink500,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 360,
        };
        Grid.SetColumn(_modsStatus, 1);
        header.Children.Add(_modsStatus);

        var refresh = BuildToolButton("Refresh");
        refresh.VerticalAlignment = VerticalAlignment.Center;
        refresh.Click += (_, _) => _ = RefreshModsViewAsync(force: true);
        Grid.SetColumn(refresh, 2);
        header.Children.Add(refresh);

        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // Body: three stacked sections inside a single scroller.
        _modsRequiredList = new StackPanel { Spacing = 8 };
        _modsRecommendedList = new StackPanel { Spacing = 8 };
        _modsBlockedList = new StackPanel { Spacing = 8 };

        var body = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 16, 0, 0),
        };
        _modsRequiredSection = ModsSection("Required", "Installed automatically when you connect", _modsRequiredList);
        body.Children.Add(_modsRequiredSection);

        _modsRecommendedSection = ModsSection("Recommended", "Optional mods the server suggests", _modsRecommendedList);
        body.Children.Add(_modsRecommendedSection);

        _modsBlockedSection = ModsSection("Blocked", "Mods this server does not allow", _modsBlockedList);
        body.Children.Add(_modsBlockedSection);

        // Shown when every section above is hidden (typical vanilla server: the
        // only manifest entry is the internal connect transport, which we never
        // surface — see IsInternalMod). Without this the tab body would be blank.
        _modsEmptyNote = new TextBlock
        {
            Text = "This server runs vanilla Solarpunk — no extra mods needed. Just connect and play.",
            Foreground = Ink500,
            FontSize = 13,
            FontStyle = FontStyle.Italic,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };
        body.Children.Add(_modsEmptyNote);

        var scroll = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        return root;
    }

    private static Border ModsSection(string title, string subtitle, StackPanel list)
    {
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(SectionHeader(title));
        stack.Children.Add(new TextBlock
        {
            Text = subtitle,
            Foreground = Ink500,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 4),
        });
        stack.Children.Add(list);

        var panel = Panel(stack);
        panel.Padding = new Thickness(16);
        return panel;
    }

    private void SetModsPlaceholder(StackPanel list, string text)
    {
        list.Children.Clear();
        list.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = Ink500,
            FontSize = 13,
            FontStyle = FontStyle.Italic,
        });
    }

    // Fetch the selected server's manifest and repaint the three sections.
    // Crash-safe: no selection, offline server, null manifest, and HTTP
    // faults all resolve to a friendly placeholder rather than throwing.
    private async Task RefreshModsViewAsync(bool force = false)
    {
        if (_modsRequiredList is null) return; // view not built yet

        var server = _selected;
        if (server is null)
        {
            _modsLoadedFor = null;
            _modsLoadCts?.Cancel();
            _modsHeading.Text = "Mods";
            _modsSubheading.Text = "Select a server to see its mods";
            _modsStatus.Text = "";
            SetModsPlaceholder(_modsRequiredList, "No server selected.");
            SetModsPlaceholder(_modsRecommendedList, "No server selected.");
            SetModsPlaceholder(_modsBlockedList, "No server selected.");
            _modsRequiredSection.IsVisible = true;
            _modsRecommendedSection.IsVisible = true;
            _modsBlockedSection.IsVisible = true;
            _modsEmptyNote.IsVisible = false;
            return;
        }

        // Skip a redundant refetch if we already have this server loaded,
        // unless the caller forced it (the Refresh button).
        if (!force && ReferenceEquals(_modsLoadedFor, server))
            return;

        _modsHeading.Text = server.DisplayName;
        _modsSubheading.Text = server.Endpoint;
        _modsStatus.Text = "Loading mods…";

        // Cancel any in-flight load (rapid server switching / spam-refresh).
        _modsLoadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _modsLoadCts = cts;
        var ct = cts.Token;

        SolarpunkHttpClient.Manifest? manifest = null;
        try
        {
            using var http = new SolarpunkHttpClient(server.Host, server.HttpPort, rconPassword: "");
            manifest = await http.GetManifestAsync(ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return; // superseded by a newer load
        }
        catch (Exception ex)
        {
            SolarpunkLog.Info($"Mods view: manifest fetch failed for {server.Host}:{server.HttpPort}: {ex.GetType().Name}: {ex.Message}");
        }

        if (ct.IsCancellationRequested || !ReferenceEquals(_modsLoadCts, cts))
            return; // a newer refresh won the race

        _modsLoadedFor = server;
        PaintManifest(server, manifest);
    }

    private void PaintManifest(ServerEntry server, SolarpunkHttpClient.Manifest? manifest)
    {
        if (manifest is null)
        {
            _modsStatus.Text = "Couldn't reach this server's mod list. It may be offline or running an older build.";
            SetModsPlaceholder(_modsRequiredList, "Mod list unavailable.");
            SetModsPlaceholder(_modsRecommendedList, "Mod list unavailable.");
            SetModsPlaceholder(_modsBlockedList, "Mod list unavailable.");
            _modsRequiredSection.IsVisible = true;
            _modsRecommendedSection.IsVisible = true;
            _modsBlockedSection.IsVisible = true;
            _modsEmptyNote.IsVisible = false;
            return;
        }

        // The internal connect transport (solarpunk-connect) is launcher
        // plumbing, not a user-facing mod — same treatment as Beacon, whose
        // beacon-connect transport never appears in its Mods tab. The manifest
        // still lists it (protocol completeness / future non-bundled clients);
        // we just never SHOW it. Filtering is display-only: ConnectFlow stages
        // the bundled mod on every connect regardless of this list.
        var required = FilterInternalMods(manifest.Required);
        var recommended = FilterInternalMods(manifest.Recommended);
        var blocked = manifest.Blocked ?? new List<SolarpunkHttpClient.ManifestBlockedEntry>();

        _modsRequiredList.Children.Clear();
        if (required.Count == 0)
        {
            // Hide the Required section entirely when there's nothing to show —
            // for a typical server the only required entry is the internal
            // connect transport, and an empty "Required" header is just noise.
            _modsRequiredSection.IsVisible = false;
        }
        else
        {
            _modsRequiredSection.IsVisible = true;
            foreach (var entry in required)
                _modsRequiredList.Children.Add(BuildModRow(entry, required: true));
        }

        _modsRecommendedList.Children.Clear();
        if (recommended.Count == 0)
        {
            // Same: hide the Recommended section when the server suggests
            // nothing (matches Beacon's behavior).
            _modsRecommendedSection.IsVisible = false;
        }
        else
        {
            _modsRecommendedSection.IsVisible = true;
            foreach (var entry in recommended)
                _modsRecommendedList.Children.Add(BuildModRow(entry, required: false));
        }

        _modsBlockedList.Children.Clear();
        if (blocked.Count == 0)
        {
            // Hide the Blocked section entirely when there's nothing to show —
            // it's the least relevant section and an empty card is just noise.
            _modsBlockedSection.IsVisible = false;
        }
        else
        {
            _modsBlockedSection.IsVisible = true;
            foreach (var entry in blocked)
                _modsBlockedList.Children.Add(BuildBlockedRow(entry, installedLocally: false));
        }

        int total = required.Count + recommended.Count;
        _modsStatus.Text = total == 0
            ? "Vanilla server — no mods."
            : $"{required.Count} required · {recommended.Count} recommended";

        // If every section collapsed (the common vanilla-server case), show a
        // single friendly line instead of a blank body.
        _modsEmptyNote.IsVisible = total == 0 && blocked.Count == 0;
    }

    // Internal launcher plumbing that must never show up in the customer-facing
    // Mods list. solarpunk-connect is the direct-IP join transport: bundled with
    // the launcher and auto-staged by ConnectFlow on every connect — it's part of
    // how joining works, not a mod the player chooses or needs to know about.
    // (Beacon hides its equivalent beacon-connect transport the same way: it
    // simply never appears in the user-facing mod list.)
    internal static bool IsInternalMod(SolarpunkHttpClient.ManifestModEntry entry) =>
        string.Equals(entry?.Id, "solarpunk-connect", StringComparison.OrdinalIgnoreCase)
        || string.Equals(entry?.Id, "SolarpunkConnect", StringComparison.OrdinalIgnoreCase);

    internal static List<SolarpunkHttpClient.ManifestModEntry> FilterInternalMods(
        List<SolarpunkHttpClient.ManifestModEntry>? entries) =>
        (entries ?? new List<SolarpunkHttpClient.ManifestModEntry>())
            .Where(e => e is not null && !IsInternalMod(e))
            .ToList();

    // Read-only manifest row. Solarpunk installs the bundled connect mod via
    // ConnectFlow at launch (not a per-instance manifest-driven installer), so
    // this view surfaces the server's declared mods without an on-disk
    // installed-state cross-check: required = installs on connect, recommended
    // = optional.
    private Control BuildModRow(
        SolarpunkHttpClient.ManifestModEntry entry,
        bool required)
    {
        var (statusText, statusBrush, statusIcon) =
            required ? ("Installs on connect", Sun600, IconDownload)
            : ("Optional", Ink500, IconDownload);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
        };

        var left = new StackPanel { Spacing = 2 };

        var titleRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
        };
        titleRow.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(entry.Name) ? entry.Id : entry.Name,
            Foreground = Ink900,
            FontSize = 14,
            FontWeight = FontWeight.Medium,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (!string.IsNullOrWhiteSpace(entry.Version))
            titleRow.Children.Add(new TextBlock
            {
                Text = "v" + entry.Version,
                Foreground = Ink500,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Bottom,
            });
        left.Children.Add(titleRow);

        if (!string.IsNullOrWhiteSpace(entry.Description))
            left.Children.Add(new TextBlock
            {
                Text = entry.Description,
                Foreground = Ink500,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });

        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        var status = StatusPill(statusText, statusBrush, statusIcon);
        status.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(status, 1);
        grid.Children.Add(status);

        return new Border
        {
            Background = Card,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10),
            Child = grid,
        };
    }

    private Control BuildBlockedRow(SolarpunkHttpClient.ManifestBlockedEntry entry, bool installedLocally)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
        };

        var left = new StackPanel { Spacing = 2 };
        left.Children.Add(new TextBlock
        {
            Text = entry.Id,
            Foreground = Ink900,
            FontSize = 14,
            FontWeight = FontWeight.Medium,
        });
        if (!string.IsNullOrWhiteSpace(entry.Reason))
            left.Children.Add(new TextBlock
            {
                Text = entry.Reason,
                Foreground = Ink500,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        // If a blocked mod happens to be installed locally, flag it loudly —
        // it won't be auto-removed (v1 is informational), but the player
        // should know the server objects to it.
        var pill = installedLocally
            ? StatusPill("Installed — remove", Red500, IconBlocked)
            : StatusPill("Blocked", Coral500, IconBlocked);
        pill.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(pill, 1);
        grid.Children.Add(pill);

        return new Border
        {
            Background = Card,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10),
            Child = grid,
        };
    }

    private static Border StatusPill(string text, IBrush brush, string icon)
    {
        var content = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(new TextBlock
        {
            Text = icon,
            FontFamily = FluentIconFont,
            FontSize = 12,
            Foreground = brush,
            VerticalAlignment = VerticalAlignment.Center,
        });
        content.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = brush,
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            VerticalAlignment = VerticalAlignment.Center,
        });

        return new Border
        {
            Background = Cream50,
            BorderBrush = brush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 4),
            Child = content,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 0, 0, 0),
        };
    }

    private void LoadSavedServers()
    {
        foreach (var rec in ServerStore.Load())
            _servers.Add(new ServerEntry(rec.Label, rec.Host, rec.Port, rec.QueryPort,
                rec.Password, rec.LastPlayerName, rec.AdminPassword));

        // No default-server seeding. Previous versions seeded a local test
        // server when the list was empty, which both (a) confused new users
        // with a server that points at a localhost dev box that doesn't
        // exist, and (b) silently overwrote
        // the saved-and-then-lost list when ServerStore.Load() failed to
        // parse an existing servers.json (since the catch returned new()).
        // Empty server list now stays empty until the user adds one.

        // Restore last-selected server if it's still in the list. Falls back
        // to the first entry on a clean install or if the saved endpoint
        // no longer matches anything.
        if (_servers.Count > 0)
        {
            var lastEndpoint = _settings.LastSelectedEndpoint;
            var restored = !string.IsNullOrWhiteSpace(lastEndpoint)
                ? _servers.FirstOrDefault(s => string.Equals(s.Endpoint, lastEndpoint, StringComparison.OrdinalIgnoreCase))
                : null;
            _serverList.SelectedItem = restored ?? _servers[0];
            // Initial RefreshAllAsync now fires from the Opened event handler
            // (see constructor) so its UI dispatch doesn't queue behind the
            // rest of constructor work. Calling it here too would just
            // double-fire — the post-Opened call is the only one we need.
        }
    }

    private bool PersistServers()
    {
        var saved = ServerStore.TrySave(
            _servers.Select(s => new ServerStore.ServerRecord(
                s.Label, s.Host, s.Port, s.QueryPort, s.Password, s.LastPlayerName, s.AdminPassword)),
            out var error);
        if (!saved)
        {
            _statusLine.Text = "Server list save failed";
            _activityLaunch.Text = $"Could not save server list: {error}";
        }

        return saved;
    }

    private async Task ShowAddServerDialogAsync()
    {
        var dialog = new AddServerDialog();
        var server = await dialog.ShowDialog<ServerEntry?>(this);
        if (server is null)
            return;

        _servers.Add(server);
        var saved = PersistServers();
        _serverList.SelectedItem = server;
        _statusLine.Text = saved ? $"Added {server.Endpoint}" : "Added, but save failed";
        _activityLaunch.Text = saved
            ? $"Added {server.Endpoint}; checking status."
            : $"Added {server.Endpoint}; server list was not saved. Checking status.";
        await PingOneAsync(server);
    }

    /// <summary>
    /// Edit-server flow. Lets customers update a saved entry's label / host /
    /// port / join password without the delete-and-re-add dance. Mutates the
    /// existing ServerEntry instance in-place (rather than swapping it in the
    /// collection) so existing bindings — status fields, hero header — keep
    /// pointing at the same object.
    ///
    /// Admin (RCON) password is cleared on a host/port change — that
    /// credential is targeted at the OLD host, and silently shipping it
    /// to a different host is an auth-material leak. Customers re-enter
    /// from the World menu's first admin click, same flow as a brand-
    /// new server.
    ///
    /// Refuses host/port edits that collide with another saved entry's
    /// endpoint.
    /// </summary>
    private async Task ShowEditServerDialogAsync()
    {
        var server = _selected;
        if (server is null)
        {
            _statusLine.Text = "Select a server first";
            _activityLaunch.Text = "Select a server in the list to edit it.";
            return;
        }

        var dialog = new EditServerDialog(server);
        var result = await dialog.ShowDialog<EditServerDialog.EditResult?>(this);
        if (result is null)
            return;

        var oldEndpoint = server.Endpoint;
        var newEndpoint = $"{result.Host}:{result.Port}";
        var endpointChanged = !string.Equals(oldEndpoint, newEndpoint, StringComparison.OrdinalIgnoreCase);

        // Duplicate-endpoint guard. Add Server has the same de-dupe rule;
        // applying it here too keeps character-store + settings lookups
        // unambiguous. Self-match (editing without moving) is allowed.
        if (endpointChanged && _servers.Any(s =>
            !ReferenceEquals(s, server) &&
            string.Equals(s.Host, result.Host, StringComparison.OrdinalIgnoreCase) &&
            s.Port == result.Port))
        {
            _statusLine.Text = "Endpoint already saved";
            _activityLaunch.Text = $"Another saved server already uses {newEndpoint}; pick a different host/port or remove the duplicate first.";
            return;
        }

        server.Label = result.Label;
        server.Host = result.Host;
        if (server.Port != result.Port)
        {
            // QueryPort is derived as Port + 2 in the constructor. The
            // ctor's only override path is panel-side; in-launcher edits
            // re-derive automatically so customers don't get stranded on
            // a stale +2 offset against a port they just changed.
            server.Port = result.Port;
            server.QueryPort = result.Port + 2;
        }
        server.Password = result.Password;

        if (endpointChanged)
        {
            // Endpoint change invalidates the cached IsPasswordProtected /
            // IsReachable from the prior host. Reset to null (unknown)
            // so the next Connect's pre-flight check forces a fresh
            // A2S query against the new endpoint instead of trusting
            // stale state from the old one.
            server.IsPasswordProtected = null;
            server.IsReachable = false;

            // Clear AdminPassword. The old credential was targeted at the
            // old host; carrying it forward could ship the customer's
            // RCON password to a completely different operator.
            server.AdminPassword = null;

            // Migrate the endpoint-keyed last-selected setting.
            if (string.Equals(_settings.LastSelectedEndpoint, oldEndpoint, StringComparison.OrdinalIgnoreCase))
                _settings.LastSelectedEndpoint = newEndpoint;
            _settings.Save(out _);
        }

        var saved = PersistServers();
        _statusLine.Text = saved ? $"Saved {server.Endpoint}" : "Saved, but persist failed";
        _activityLaunch.Text = saved
            ? (endpointChanged
                ? $"Updated to {newEndpoint}. Admin (RCON) password cleared."
                : $"Updated {server.Endpoint}.")
            : $"Updated {server.Endpoint}; server list was not written to disk.";

        // Re-ping in both cases. A fresh A2S round-trips the new
        // IsPasswordProtected signal for the pre-flight check, and on
        // same-endpoint edits it confirms our metadata change didn't
        // somehow break the link.
        RefreshHero();
        await PingOneAsync(server);
    }

    /// <summary>
    /// Handle a `solarpunk://add?host=X&port=Y&label=Z` URL. Source is
    /// either argv (Program.PendingSolarpunkUrl on first launch) or the
    /// activation-pipe "url <uri>" message from a second launch while
    /// we're already running. Adds the server if it isn't already
    /// known, selects it, kicks off a status ping. If a same-endpoint
    /// entry already exists we don't duplicate — just select + ping it.
    /// Silently ignores malformed URLs; the panel-side button is the
    /// only source of these and if it ever ships a bad one we'd rather
    /// be a no-op than spam dialogs.
    /// </summary>
    private static readonly string[] SolarpunkUrlSecretParamNames =
        new[] { "server_password", "admin_password", "rcon_password" };

    private static string RedactSolarpunkUrlForLog(string url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        var redacted = url;
        foreach (var name in SolarpunkUrlSecretParamNames)
        {
            // Case-insensitive match on `name=<value>` up to the next '&'
            // or end of string. Keeps the key visible for triage but
            // blanks the value.
            redacted = System.Text.RegularExpressions.Regex.Replace(
                redacted,
                $@"(?i)(\b{System.Text.RegularExpressions.Regex.Escape(name)}=)[^&\s]*",
                "$1<redacted>");
        }
        return redacted;
    }

    // Parse a host:port out of a solarpunk:// add link so the panel hand-off can
    // pre-fill / route the address. Format: solarpunk://add?host=X&port=Y[&label=Z].
    // Consumed by Program.cs (PendingAddress).
    public static string? AddressFromSolarpunkUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var host = Regex.Match(url, @"[?&]host=([^&]+)", RegexOptions.IgnoreCase);
        if (!host.Success) return null;
        var h = Uri.UnescapeDataString(host.Groups[1].Value);
        var portM = Regex.Match(url, @"[?&]port=(\d{1,5})", RegexOptions.IgnoreCase);
        return portM.Success ? $"{h}:{portM.Groups[1].Value}" : h;
    }

    private async Task AddServerFromSolarpunkUrlAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        // Redact credentials from logged URLs. The solarpunk://add link can
        // carry server_password / admin_password / rcon_password in the
        // query; we must never log those verbatim, because Windows passes
        // the entire activation URL as argv to the launcher (visible in
        // Process Explorer / EDR / crash dumps already; we at least
        // prevent leaking it into our own log file too).
        var safeUrl = RedactSolarpunkUrlForLog(url);

        Uri uri;
        try { uri = new Uri(url); }
        catch { SolarpunkLog.Warn($"solarpunk:// URL parse failed: {safeUrl}"); return; }

        if (!string.Equals(uri.Scheme, "solarpunk", StringComparison.OrdinalIgnoreCase))
            return;

        // Solarpunk URLs use the host part as a verb (e.g. "add"); future
        // verbs (e.g. "connect" to launch immediately) plug in here.
        var verb = uri.Host;
        if (!string.Equals(verb, "add", StringComparison.OrdinalIgnoreCase))
        {
            SolarpunkLog.Warn($"solarpunk:// unknown verb '{verb}' in {safeUrl}");
            return;
        }

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var host = (query["host"] ?? "").Trim();
        var portText = (query["port"] ?? "").Trim();
        var label = (query["label"] ?? "").Trim();
        var adminPassword = (query["admin_password"] ?? query["rcon_password"] ?? "").Trim();
        // server_password is the game-server join password (matches
        // BeaconAuthPassword in BeaconServer/appsettings.json). Sent by
        // the panel's <solarpunk://add> builder so customers who click
        // "Open in Solarpunk" on a passworded server land with the join
        // password pre-populated, instead of getting kicked by
        // BeaconAuth.lua's K2_PostLogin gate. Read byte-exact (NOT
        // .Trim()'d) because the launcher writes the password to
        // client-password.txt byte-exact, and BeaconAuth's CONFIGURED_
        // PASSWORD comparison is byte-exact post-percent-decode.
        //
        // Distinguish three states:
        //   serverPasswordParamPresent=false: legacy panel emission, no
        //     param at all → preserve existing saved password (don't
        //     clobber user-typed override).
        //   present + non-empty: rotate to the new value.
        //   present + empty: panel cleared passlock → clear saved
        //     password too, so the launcher doesn't keep sending a
        //     stale value to an open server (BeaconAuth would treat
        //     the stale ?Password= as an attempt and either log a
        //     mismatch or ignore depending on state).
        var serverPasswordParamPresent = query.AllKeys != null &&
            Array.Exists(query.AllKeys, k => string.Equals(k, "server_password", StringComparison.OrdinalIgnoreCase));
        var serverPasswordRaw = query["server_password"];
        var serverPassword = string.IsNullOrEmpty(serverPasswordRaw) ? null : serverPasswordRaw;
        if (string.IsNullOrEmpty(label)) label = host;

        if (string.IsNullOrEmpty(host) || !int.TryParse(portText, out var port) || port <= 0 || port > 65535)
        {
            SolarpunkLog.Warn($"solarpunk://add missing/bad host/port: {safeUrl}");
            _statusLine.Text = "Bad solarpunk:// link";
            _activityLaunch.Text = "The solarpunk:// link was missing a valid host or port.";
            return;
        }

        // De-dupe by endpoint (host:port). If the customer clicks the
        // panel link twice we don't want a duplicate row, just bring
        // attention to the existing entry. Refresh credentials from the
        // new link so a panel-side password rotation flows through on
        // the next click, without overwriting a user-typed override
        // with an empty link param (e.g. an older panel revision that
        // never sends server_password should not blank out a password
        // the user already saved).
        var existing = _servers.FirstOrDefault(s =>
            string.Equals(s.Host, host, StringComparison.OrdinalIgnoreCase) && s.Port == port);
        if (existing is not null)
        {
            var changed = false;
            if (!string.IsNullOrEmpty(adminPassword))
            {
                existing.AdminPassword = adminPassword;
                changed = true;
            }
            if (serverPasswordParamPresent)
            {
                // Set OR clear, depending on whether the link's value is
                // empty. This handles the panel-cleared-passlock case
                // (existing.Password was "secret", link now sends
                // server_password= empty → clear existing.Password).
                // Legacy links without the param at all are handled
                // above: serverPasswordParamPresent=false → no update.
                existing.Password = serverPassword;
                changed = true;
            }
            if (changed) PersistServers();
            _serverList.SelectedItem = existing;
            _statusLine.Text = $"Selected {existing.Endpoint}";
            _activityLaunch.Text = $"Already had {existing.Endpoint}; selected it.";
            await PingOneAsync(existing);
            return;
        }

        var server = new ServerEntry(label, host, port,
            password: serverPassword,
            adminPassword: string.IsNullOrEmpty(adminPassword) ? null : adminPassword);
        _servers.Add(server);
        var saved = PersistServers();
        _serverList.SelectedItem = server;
        _statusLine.Text = saved ? $"Added {server.Endpoint}" : "Added, but save failed";
        _activityLaunch.Text = saved
            ? $"Added {server.Endpoint} from panel link; checking status."
            : $"Added {server.Endpoint} from panel link; server list was not saved. Checking status.";
        SolarpunkLog.Info($"solarpunk://add: {server.Endpoint} ({label})");
        await PingOneAsync(server);
    }

    private async Task ShowSettingsDialogAsync()
    {
        var dialog = new SettingsDialog(_settings);
        var updated = await dialog.ShowDialog<LauncherSettings?>(this);
        if (updated is null)
            return;

        _settings.GamePathOverride = updated.GamePathOverride;
        _settings.LogDirectory = updated.LogDirectory;
        _settings.StartOnWindowsLogin = updated.StartOnWindowsLogin;
        _settings.CheckForUpdatesOnStart = updated.CheckForUpdatesOnStart;
        if (!_settings.Save(out var saveError))
        {
            _statusLine.Text = "Settings save failed";
            _activityLaunch.Text = $"Could not save settings: {saveError}";
            return;
        }

        // Sync HKCU\Run with the boot-at-startup checkbox state. Errors here
        // are non-fatal — saved setting still persists; user can retry.
        if (_settings.StartOnWindowsLogin)
        {
            // Environment.ProcessPath is the real exe path and is single-file-safe
            // (Assembly.Location returns empty in a single-file publish — IL3000).
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exe))
                StartupRegistration.Enable(exe, out _);
        }
        else
        {
            StartupRegistration.Disable(out _);
        }

        _statusLine.Text = "Settings saved";
        _activityLaunch.Text = "Settings saved.";
        RefreshPathChecks();
    }

    private async Task ShowAboutDialogAsync()
    {
        var dialog = new AboutDialog();
        await dialog.ShowDialog(this);
    }

    private void RemoveSelected()
    {
        if (_selected is null)
        {
            _statusLine.Text = "Select a server first";
            return;
        }

        var removed = _selected;
        _servers.Remove(removed);
        var saved = PersistServers();
        SelectServer(_servers.FirstOrDefault());
        _serverList.SelectedItem = _selected;
        _statusLine.Text = saved ? $"Removed {removed.Endpoint}" : "Removed, but save failed";
        _activityLaunch.Text = saved
            ? $"Removed {removed.Endpoint}."
            : $"Removed {removed.Endpoint}; server list was not saved.";
    }

    // Solarpunk shipping process image names. Solarpunk launches the Steam
    // build; the GDK variant is included defensively.
    private static readonly string[] GameProcessImageNames =
    {
        "SolarpunkSteam-Win64-Shipping.exe",
        "Solarpunk-Win64-Shipping.exe",
        "Solarpunk-WinGDK-Shipping.exe",
    };

    /// <summary>
    /// Polled every 2 seconds while the launcher is open. Detects a running
    /// Solarpunk process and flips the Connect button + status banner to
    /// "Solarpunk is running" so the customer doesn't accidentally Connect
    /// twice and spawn a second game over the first.
    /// </summary>
    private void RefreshGameRunningState()
    {
        bool running;
        try
        {
            running = GameLaunchService.IsGameRunning();
        }
        catch (Exception ex)
        {
            // ETW/perfcounter access can throw on locked-down machines.
            // Assume not-running rather than blocking the Connect button.
            SolarpunkLog.Info($"RefreshGameRunningState: probe threw: {ex.GetType().Name}: {ex.Message}");
            running = false;
        }
        if (running == _gameRunning) return;

        SolarpunkLog.Info($"RefreshGameRunningState: _gameRunning {_gameRunning} -> {running}");
        _gameRunning = running;
        ApplyConnectButtonState();

        if (_connectLaunchCts is not null)
            return;

        // Keep the activity line in sync so the customer has a visible
        // explanation for the disabled Connect button.
        if (running)
        {
            _activityLaunch.Text = "Solarpunk is running. Quit the game to launch a new session.";
        }
        else
        {
            // Don't overwrite Connect-flow messages with "ready"; only
            // clear back to a neutral state when we were the ones who
            // posted the "running" banner.
            if (string.Equals(_activityLaunch.Text,
                "Solarpunk is running. Quit the game to launch a new session.",
                StringComparison.Ordinal))
            {
                _activityLaunch.Text = "";
            }
        }
    }

    private bool IsGameRunningForUpdateGuard()
    {
        try
        {
            var running = GameLaunchService.IsGameRunning();
            _gameRunning = running;
            ApplyConnectButtonState();
            return running;
        }
        catch (Exception ex)
        {
            SolarpunkLog.Warn($"Game update guard probe failed: {ex.GetType().Name}: {ex.Message}");
            return _gameRunning;
        }
    }

    private void ApplyConnectButtonState()
    {
        if (_connectButton is null)
            return;

        // Connect when idle; Close Solarpunk from the moment a launch starts
        // through full game shutdown.
        var inFlight = _connectLaunchCts is not null;
        var stopVisible = inFlight || _gameRunning;

        _connectButton.Content = ButtonContent(IconConnect, "Connect");
        _connectButton.IsEnabled = !stopVisible;
        _connectButton.IsVisible = !stopVisible;

        if (_closeGameButton is not null)
        {
            _closeGameButton.IsVisible = stopVisible;
        }
    }

    private static IEnumerable<System.Diagnostics.Process> GetGameProcesses()
    {
        foreach (var imageName in GameProcessImageNames)
        {
            foreach (var process in System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(imageName)))
                yield return process;
        }
    }

    private async Task CloseRunningGameAsync()
    {
        // The Close Solarpunk button covers BOTH the launch-wait window
        // and the running-game window. Cancel any in-flight launch first
        // so the connect chain unwinds cleanly. Ask the game to close first;
        // kill only if the process refuses to exit.
        if (_connectLaunchCts is not null)
        {
            SolarpunkLog.Info("CloseGame: canceling in-flight launch");
            try { _connectLaunchCts.Cancel(); } catch { }
        }

        _statusLine.Text = "Closing Solarpunk";
        _activityLaunch.Text = "Closing Solarpunk.";
        try
        {
            var processes = GetGameProcesses().ToList();
            if (processes.Count == 0)
            {
                _statusLine.Text = "Solarpunk was not running";
                _activityLaunch.Text = "";
                RefreshGameRunningState();
                return;
            }

            int closeRequested = 0;
            foreach (var p in processes)
            {
                try
                {
                    if (!p.HasExited && p.CloseMainWindow())
                        closeRequested++;
                }
                catch { }
            }

            var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
            while (DateTimeOffset.UtcNow < deadline && processes.Any(p => !HasExited(p)))
            {
                await Task.Delay(250).ConfigureAwait(true);
            }

            int killed = 0;
            int exited = 0;
            foreach (var p in processes)
            {
                if (HasExited(p))
                {
                    exited++;
                    try { p.Dispose(); } catch { }
                    continue;
                }

                try
                {
                    p.Kill(entireProcessTree: true);
                    killed++;
                }
                catch { }
                try { p.Dispose(); } catch { }
            }

            SolarpunkLog.Info($"CloseGame: closeRequested={closeRequested} exited={exited} killed={killed}");
            _statusLine.Text = killed > 0
                ? $"Closed {exited + killed} Solarpunk process(es)"
                : $"Closed {exited} Solarpunk process(es)";
            _activityLaunch.Text = "";
        }
        catch (Exception ex)
        {
            SolarpunkLog.Info($"CloseGame: failed: {ex.Message}");
            _statusLine.Text = "Close failed";
            _activityLaunch.Text = $"Could not close Solarpunk: {ex.Message}";
        }
        // Force the process watcher to refresh ASAP rather than waiting up to
        // 2s for the next tick.
        RefreshGameRunningState();
    }

    private static bool HasExited(System.Diagnostics.Process process)
    {
        try { return process.HasExited; }
        catch { return true; }
    }

    private async Task RefreshAllAsync(bool silent = false)
    {
        if (_servers.Count == 0)
        {
            if (!silent) _statusLine.Text = "No saved servers";
            return;
        }

        if (Interlocked.Exchange(ref _refreshAllInFlight, 1) == 1)
            return;

        try
        {
            if (!silent)
            {
                _statusLine.Text = "Refreshing";
                _activityLaunch.Text = "";
            }
            await Task.WhenAll(_servers.ToList().Select(PingOneAsync));
            if (!silent)
            {
                _statusLine.Text = "Ready";
                _activityLaunch.Text = "";
            }
        }
        finally
        {
            Interlocked.Exchange(ref _refreshAllInFlight, 0);
            UpdateSelectedServerPollInterval();
        }
    }

    private async Task RefreshSelectedAsync()
    {
        var entry = _selected;
        if (entry is null || entry.IsQuerying || Volatile.Read(ref _refreshAllInFlight) == 1)
        {
            UpdateSelectedServerPollInterval();
            return;
        }

        if (Interlocked.Exchange(ref _refreshSelectedInFlight, 1) == 1)
            return;

        try
        {
            await PingOneAsync(entry);
        }
        finally
        {
            Interlocked.Exchange(ref _refreshSelectedInFlight, 0);
            UpdateSelectedServerPollInterval();
        }
    }

    private void UpdateSelectedServerPollInterval()
    {
        if (_selectedServerPoll is null) return;
        var interval = _selected is not null && _selected.IsReachable
            ? TimeSpan.FromSeconds(10)
            : TimeSpan.FromSeconds(30);
        if (_selectedServerPoll.Interval != interval)
            _selectedServerPoll.Interval = interval;
    }

    private async Task PingOneAsync(ServerEntry entry)
    {
        entry.MarkQuerying();
        RefreshHero();

        // v0.3.56: Snapshot the endpoint at ping start. The Edit Server
        // dialog can mutate entry.Host / entry.Port mid-ping; without this
        // check, a stale query against the old endpoint would land and
        // overwrite IsPasswordProtected / IsReachable for the new one
        // (e.g. moving from a passworded server to an open one could
        // silently suppress the next pre-flight password prompt).
        var pingHost = entry.Host;
        var pingPort = entry.Port;
        var pingQueryPort = entry.QueryPort;

        SolarpunkLog.Debug($"Probe: start {pingHost} gameplay={pingPort} query={pingQueryPort} http={entry.HttpPort}");

        // Run the A2S query + /health probe IN PARALLEL — otherwise the
        // "Checking" dot stays cyan for 3s + 4s = 7s worst case while we
        // wait sequentially. Parallel cuts that to max(3, 4) = ~4s.
        // try/finally guarantees ApplyInfo clears IsQuerying even if one
        // of the probes throws an unhandled exception (would otherwise
        // leave the dot stuck on cyan).
        A2sClient.A2sInfo? info = null;
        HealthProbe health = HealthProbe.Offline;
        List<PlayerRow> players = new();
        try
        {
            var a2sTask = A2sClient.QueryAsync(pingHost, pingQueryPort);
            var healthTask = ProbeHealthAsync(entry);
            var playersTask = ProbePlayersAsync(entry);
            await Task.WhenAll(a2sTask, healthTask, playersTask).ConfigureAwait(false);
            info = a2sTask.Result;
            health = healthTask.Result;
            players = playersTask.Result;
            SolarpunkLog.Debug($"Probe: result {pingHost}:{pingPort} a2s={(info is not null ? "ok" : "none")} " +
                $"health={(health.IsOnline ? "online" : "offline")} ver={health.Version ?? "?"} " +
                $"players={players.Count} a2sPlayers={info?.Players.ToString() ?? "?"}");
        }
        catch (Exception ex)
        {
            // fall through to ApplyInfo(null)
            SolarpunkLog.Debug($"Probe: {pingHost}:{pingPort} threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Drop the result if the entry's endpoint changed while
                // the ping was in flight (Edit dialog took it elsewhere).
                // Marking IsQuerying false here so the UI doesn't get
                // stuck on "Refreshing"; the next ping for the new
                // endpoint will replace the state cleanly.
                if (!string.Equals(entry.Host, pingHost, StringComparison.OrdinalIgnoreCase)
                    || entry.Port != pingPort
                    || entry.QueryPort != pingQueryPort)
                {
                    entry.IsQuerying = false;
                    SolarpunkLog.Info($"Ping result dropped: endpoint changed mid-ping ({pingHost}:{pingPort} → {entry.Host}:{entry.Port})");
                    return;
                }

                if (info is null && health.IsOnline)
                {
                    var playerCount = health.PlayerCount ?? players.Count;
                    var maxPlayers = health.MaxPlayers ?? (entry.MaxPlayers > 0 ? entry.MaxPlayers : 4);
                    entry.ApplyHttpOnline(health.ServerName, playerCount, maxPlayers);
                    SolarpunkLog.Info($"Refresh: A2S failed for {entry.Host}:{entry.QueryPort}; HTTP health kept server online");
                }
                else
                {
                    entry.ApplyInfo(info);
                }
                entry.ServerVersion = health.Version;
                entry.GameBuild = health.GameBuild;
                entry.IsOutdated = health.Outdated;
                entry.IsLauncherOutdated = health.LauncherOutdated;
                if (ReferenceEquals(entry, _selected))
                {
                    RefreshHero();
                    RenderHeroPlayerRoster(players);
                }
            });
        }
    }

    private void RenderHeroPlayerRoster(List<PlayerRow> players)
    {
        if (_heroPlayerRoster is null) return;
        _heroPlayerRoster.Children.Clear();
        foreach (var p in players)
        {
            var row = new TextBlock
            {
                Foreground = Ink500,
                FontSize = 12,
                Text = p.PingMs is null
                    ? $"  {p.Name} — {FormatDuration(p.ConnectedSeconds)}"
                    : $"  {p.Name} — {FormatDuration(p.ConnectedSeconds)} · {p.PingMs} ms",
            };
            _heroPlayerRoster.Children.Add(row);
        }
    }

    private sealed record PlayerRow(string Name, long ConnectedSeconds, int? PingMs);

    /// <summary>
    /// Public /api/v1/players probe. Returns the live roster reported by
    /// the server's plugin (names + connect duration + ping). No auth.
    /// </summary>
    private async Task<List<PlayerRow>> ProbePlayersAsync(ServerEntry entry)
    {
        try
        {
            using var resp = await ServerStatusHttp.GetAsync(
                BuildServerStatusUri(entry, "api/v1/players")).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return new();
            using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("players", out var arr)) return new();
            var rows = new List<PlayerRow>();
            foreach (var p in arr.EnumerateArray())
            {
                var name = p.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                long secs = p.TryGetProperty("connected_seconds", out var c) && c.TryGetInt64(out var cs) ? cs : 0;
                int? ping = p.TryGetProperty("ping_ms", out var pg) && pg.TryGetInt32(out var pi) && pi > 0 ? pi : null;
                if (!string.IsNullOrEmpty(name)) rows.Add(new PlayerRow(name, secs, ping));
            }
            return rows;
        }
        catch { return new(); }
    }

    private static string FormatDuration(long seconds)
    {
        if (seconds < 60) return $"{seconds}s";
        if (seconds < 3600) return $"{seconds / 60}m";
        return $"{seconds / 3600}h{(seconds % 3600) / 60}m";
    }

    /// <summary>
    private sealed record HealthProbe(
        bool IsOnline,
        string? Version,
        string? GameBuild,
        bool Outdated,
        bool LauncherOutdated,
        string? ServerName,
        int? PlayerCount,
        int? MaxPlayers)
    {
        public static readonly HealthProbe Offline = new(false, null, null, false, false, null, null, null);
    }

    /// Public /api/v1/health probe. Returns the server's supervisor/game versions
    /// plus enough basic status for an HTTP fallback if A2S is blocked.
    /// </summary>
    private async Task<HealthProbe> ProbeHealthAsync(ServerEntry entry)
    {
        try
        {
            using var resp = await ServerStatusHttp.GetAsync(
                BuildServerStatusUri(entry, "api/v1/health")).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return HealthProbe.Offline;
            using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("solarpunk_version", out var bv)) return HealthProbe.Offline;
            var serverVer = bv.GetString();
            if (string.IsNullOrWhiteSpace(serverVer)) return HealthProbe.Offline;
            string? gameBuild = null;
            if (doc.RootElement.TryGetProperty("game_build", out var sb))
                gameBuild = sb.GetString();
            string? serverName = null;
            if (doc.RootElement.TryGetProperty("server_name", out var sn))
                serverName = sn.GetString();
            int? playerCount = null;
            if (doc.RootElement.TryGetProperty("player_count", out var pc) && pc.TryGetInt32(out var pcValue))
                playerCount = pcValue;
            int? maxPlayers = null;
            if (doc.RootElement.TryGetProperty("max_players", out var mp) && mp.TryGetInt32(out var mpValue))
                maxPlayers = mpValue;
            // The client launcher and SolarpunkServer are versioned on independent tracks
            // (the launcher is updated via its own AutoUpdater feed; the server is updated
            // by the host/operator). A server reporting a different version than the launcher
            // does NOT mean either is outdated, so never flag outdated from a launcher-vs-server
            // comparison — it produced a bogus "launcher outdated, update to match" warning.
            SolarpunkLog.Debug($"HealthProbe: {entry.Host}:{entry.HttpPort} online ver={serverVer} build={gameBuild ?? "?"} name='{serverName}' players={playerCount?.ToString() ?? "?"}/{maxPlayers?.ToString() ?? "?"}");
            return new HealthProbe(true, serverVer, gameBuild, false, false, serverName, playerCount, maxPlayers);
        }
        catch (Exception ex)
        {
            SolarpunkLog.Debug($"HealthProbe: {entry.Host}:{entry.HttpPort} offline ({ex.GetType().Name}: {ex.Message})");
            return HealthProbe.Offline;
        }
    }

    private static Uri BuildServerStatusUri(ServerEntry entry, string path) =>
        new UriBuilder(Uri.UriSchemeHttp, entry.Host, entry.HttpPort, path).Uri;

    private static int CompareSemver(string a, string b)
    {
        var aP = a.Split('-', 2)[0].Split('.');
        var bP = b.Split('-', 2)[0].Split('.');
        for (var i = 0; i < Math.Max(aP.Length, bP.Length); i++)
        {
            var av = i < aP.Length && int.TryParse(aP[i], out var x) ? x : 0;
            var bv = i < bP.Length && int.TryParse(bP[i], out var y) ? y : 0;
            if (av != bv) return av.CompareTo(bv);
        }
        return 0;
    }

    private void SelectServer(ServerEntry? server)
    {
        if (_selected is not null)
            _selected.PropertyChanged -= SelectedServerPropertyChanged;

        _selected = server;

        if (_selected is not null)
            _selected.PropertyChanged += SelectedServerPropertyChanged;

        UpdateSelectedServerPollInterval();

        if (_selected is not null && _selectedServerPoll is not null)
            _ = RefreshSelectedAsync();

        // Pre-fill the Console tab's host/port/password from the selected
        // server so clicking Console doesn't make the customer retype what
        // the Servers tab already knows. Admin password (RCON) comes from
        // the per-server entry; if not yet entered, customer types it.
        if (_selected is not null && _consoleHost is not null)
        {
            _consoleHost.Text = _selected.Host;
            _consolePort.Text = _selected.RconPort.ToString();
            _consolePassword.Text = _selected.AdminPassword ?? "";
        }

        // Restore the character last played on THIS server. Falls back to the
        // most recently played roster entry for the endpoint so a fresh
        // ServerEntry (re-add, solarpunk:// link) still recovers the character.
        if (_selected is not null && _characterNameBox is not null)
        {
            var name = _selected.LastPlayerName;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = CharacterStore.ForServer(_selected.Endpoint)
                    .OrderByDescending(c => c.LastPlayed ?? c.Created)
                    .FirstOrDefault()?.Name;
            }
            _characterNameBox.Text = name ?? "";
        }

        RefreshHero();
        UpdateStatus();

        // If the Mods tab is the live view, repaint it for the new selection.
        // Otherwise it lazy-loads the next time the user opens it.
        if (_modsView is not null && ReferenceEquals(_contentHost.Content, _modsView))
            _ = RefreshModsViewAsync(force: true);
    }

    private void SelectedServerPropertyChanged(object? sender, PropertyChangedEventArgs e) => RefreshHero();

    private async Task ShowWorldTransferDialogAsync()
    {
        if (_selected is null)
        {
            _statusLine.Text = "Select a server first";
            return;
        }
        // Single dialog. If no admin password is saved, the dialog shows an
        // inline "Enter admin password" prompt at the top. Avoids the
        // earlier modal-on-modal bug where the password dialog could lose
        // focus and leave the UI in a wedged state.
        await new WorldTransferDialog(_selected, _servers.ToList(), () => PersistServers()).ShowDialog(this);
    }

    // Builds the hero meta line using Inlines so the version-mismatch badge
    // can render in amber while the rest of the line stays Ink500. Layout:
    //   Solarpunk CL-NNNNNN | Solarpunk vX.Y.Z (badge) | NN ms | Password protected
    // Badge only appears when ServerVersion is known AND differs from the
    // launcher's own version.
    private void ApplyHeroMeta(ServerEntry server)
    {
        _heroMeta.Text = null;
        _heroMeta.Inlines = new InlineCollection();
        var inlines = _heroMeta.Inlines;

        var build = server.GameBuild;
        if (!string.IsNullOrEmpty(build) && build!.StartsWith("Solarpunk-", StringComparison.OrdinalIgnoreCase))
            build = build.Substring("Solarpunk-".Length);
        // Hide the build segment when the server doesn't report a real build ("unknown").
        var head = (!string.IsNullOrEmpty(build) && !build!.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            ? $"Solarpunk {build} | "
            : "Solarpunk | ";
        inlines.Add(new Run(head));

        if (!string.IsNullOrEmpty(server.ServerVersion))
        {
            inlines.Add(new Run($"Solarpunk v{server.ServerVersion}"));

            // Badge: only when there's a real launcher version to compare and
            // the two disagree. Pre-release suffixes are tolerated by
            // CompareSemver (strips at first '-').
            var launcherVer = SolarpunkLifecycle.CurrentVersion;
            // Dev / side-loaded builds carry a placeholder version (1.0.0) that isn't
            // meaningfully comparable to a released server, so the badge would always
            // read "server outdated". Skip the version badge entirely on dev builds.
            if (!string.IsNullOrEmpty(launcherVer) && !IsDevBuild())
            {
                if (server.IsOutdated)
                {
                    inlines.Add(new Run(" (server outdated — restart to update)")
                    {
                        Foreground = Coral500,
                    });
                }
                else if (server.IsLauncherOutdated)
                {
                    inlines.Add(new Run($" (launcher outdated — v{launcherVer} → update to match v{server.ServerVersion})")
                    {
                        Foreground = Coral500,
                    });
                }
            }

            inlines.Add(new Run(" | "));
        }

        var latency = server.LatencySummary;
        if (!string.IsNullOrEmpty(latency))
            inlines.Add(new Run(latency));

        if (server.IsPasswordProtected == true || !string.IsNullOrEmpty(server.Password))
            inlines.Add(new Run(" | Password protected"));
    }

    private void RefreshHero()
    {
        if (_heroName is null)
            return;

        if (_selected is null)
        {
            _heroName.Text = "No server selected";
            _heroEndpoint.Text = "Select a server from the list";
            _heroMeta.Text = "";
            _heroPlayers.Text = "No data";
            _heroPlayerFill.Value = 0;
            return;
        }

        _heroName.Text = _selected.DisplayName;
        _heroEndpoint.Text = _selected.Endpoint;
        ApplyHeroMeta(_selected);
        _heroPlayers.Text = _selected.PlayerSummary;
        _heroPlayerFill.Value = _selected.PlayerFill;
    }

    private void UpdateStatus()
    {
        if (_selected is null)
        {
            _statusLine.Text = "No server selected";
            return;
        }

        _consoleHost.Text = _selected.Host;
    }

    private void RefreshPathChecks()
    {
        // Path-check status used to live in a main-view activity strip. The
        // Settings dialog computes its own checks via the auto-detect helpers.
        // This stub stays in case anything still calls into it for
        // side-effect-free reasons.
    }

    // The connect path. Beacon's ConnectAsync did the whole launch mechanism
    // inline (process guard, password preflight, character gate, Steam-vs-direct
    // decision, elevation, write client config, Frida-inject UE4SS). Solarpunk
    // delegates the entire launch mechanism to Services/ConnectFlow, which
    // locates retail Solarpunk, installs the UE4SS runtime via a proxy DLL,
    // installs the bundled SolarpunkConnect mod, writes the connect-target file +
    // client Engine.ini, and launches the game. This method handles only the
    // UI-side concerns Beacon's did: the "game already running" guard, the
    // optional join-password preflight, and surfacing progress/errors onto the
    // status line.
    private async Task ConnectAsync()
    {
        // A click while a launch is in flight cancels the wait.
        if (_connectLaunchCts is not null)
        {
            SolarpunkLog.Info("Connect: cancellation requested by user");
            _statusLine.Text = "Canceling launch";
            _activityLaunch.Text = "Canceling the launch.";
            try { _connectLaunchCts.Cancel(); } catch { }
            return;
        }

        // Re-entry guard for the pre-launch phase (sync re-checks + the
        // password prompt run before the launch CTS is allocated).
        if (_connectInFlight)
        {
            SolarpunkLog.Info("Connect: ignoring re-entry — already in progress");
            return;
        }

        // Hard guard: if Solarpunk is already running, refuse to launch a
        // second copy. Re-poll synchronously at click time since the cached
        // _gameRunning flag lags the 2s process-watcher tick by up to 2s.
        bool nowRunning;
        try { nowRunning = GameLaunchService.IsGameRunning(); }
        catch { nowRunning = _gameRunning; }
        if (nowRunning)
        {
            SolarpunkLog.Info("Connect: blocked — Solarpunk already running (sync re-check)");
            _statusLine.Text = "Solarpunk already running";
            _activityLaunch.Text = "Solarpunk is already running. Quit the game window before clicking Connect again.";
            if (!_gameRunning)
            {
                _gameRunning = true;
                ApplyConnectButtonState();
            }
            return;
        }

        if (_selected is null)
        {
            _statusLine.Text = "Select a server first";
            _activityLaunch.Text = "Select a server before connecting.";
            return;
        }
        var server = _selected;

        // Character gate. The name is the save identity on the host (?Name= →
        // synthetic SteamID64 → save shard), so an empty name would collapse
        // every player onto the host's fallback id. Require it before launch.
        var characterName = ConnectTargetService.SanitizeCharacterName(_characterNameBox?.Text);
        if (string.IsNullOrEmpty(characterName))
        {
            _statusLine.Text = "Enter a character name";
            _activityLaunch.Text = "Enter a character name, then Sundial will connect.";
            _characterNameBox?.Focus();
            return;
        }

        _connectInFlight = true;
        try
        {
            // Join-password preflight. If the saved entry has no password but
            // A2S says the server is password-protected, prompt for it. Unknown
            // (null) forces a tight synchronous A2S query first. The entered
            // value is held locally until we commit to launching.
            string? launchPassword = server.Password;
            if (string.IsNullOrEmpty(launchPassword))
            {
                if (server.IsPasswordProtected is null)
                {
                    _activityLaunch.Text = "Checking whether server requires a password...";
                    var probeHost = server.Host;
                    var probeQueryPort = server.QueryPort;
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        var probedInfo = await A2sClient.QueryAsync(probeHost, probeQueryPort, cts.Token).ConfigureAwait(true);
                        if (probedInfo is not null
                            && string.Equals(server.Host, probeHost, StringComparison.OrdinalIgnoreCase)
                            && server.QueryPort == probeQueryPort)
                        {
                            server.IsPasswordProtected = probedInfo.IsPasswordProtected;
                        }
                    }
                    catch { /* still unknown → don't prompt */ }
                }

                if (server.IsPasswordProtected == true)
                {
                    var dialog = new JoinPasswordPromptDialog(server.Endpoint);
                    var entered = await dialog.ShowDialog<string?>(this);
                    if (entered is null)
                    {
                        _statusLine.Text = "Connect canceled";
                        _activityLaunch.Text = $"{server.Endpoint} requires a join password; nothing entered, not connecting.";
                        return;
                    }
                    launchPassword = entered;
                }
            }

            // Commit the (possibly newly prompted) password to the entry + disk.
            if (!string.Equals(server.Password, launchPassword, StringComparison.Ordinal))
            {
                server.Password = launchPassword;
                RefreshHero();
                if (!PersistServers())
                    SolarpunkLog.Warn("ConnectAsync: PersistServers failed after password update; kept in memory only");
            }

            // Persist last-selected endpoint so it restores next session.
            _settings.LastSelectedEndpoint = server.Endpoint;
            _settings.LastAddress = server.Endpoint;
            _settings.Save(out _);

            // Remember the character per server + roster it, so switching
            // servers (or reinstalling) restores the right identity.
            if (!string.Equals(server.LastPlayerName, characterName, StringComparison.Ordinal))
            {
                server.LastPlayerName = characterName;
                if (!PersistServers())
                    SolarpunkLog.Warn("ConnectAsync: PersistServers failed after character update; kept in memory only");
            }
            CharacterStore.TouchPlayed(characterName, server.Endpoint);

            // Build the typed connect target Solarpunk's engine consumes. The
            // character name rides the connect line as ?Name= (the server-side
            // save-identity contract); the join password rides the same name
            // channel as "<char>__SPPW__<pw>" (?Password= is unreadable by the
            // server-side gate — see ConnectTargetService.ConnectLine).
            var target = new ConnectTargetService.Target(server.Host, server.Port, characterName, server.Password);

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(_windowClosedCts.Token);
            _connectLaunchCts = connectCts;
            ApplyConnectButtonState();

            _statusLine.Text = $"Launching {server.Endpoint}";
            _activityLaunch.Text = "Preparing Solarpunk...";
            SolarpunkLog.Info($"Connect: target={server.Endpoint} (join via ConnectFlow)");

            try
            {
                // ConnectFlow does the synchronous, file/process-heavy work
                // (locate the game, install UE4SS + the connect mod, write the target
                // + Engine.ini, launch). Run it off the UI thread; pump its
                // progress onto the status line via the UI dispatcher.
                var gamePathOverride = string.IsNullOrWhiteSpace(_settings.GamePathOverride)
                    ? null
                    : _settings.GamePathOverride;
                var flow = new ConnectFlow(gamePathOverride);

                var result = await Task.Run(() => flow.Connect(target, p =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        _activityLaunch.Text = p.Message;
                        SolarpunkLog.Info($"Connect[{p.Stage}]: {p.Message}");
                    });
                }), connectCts.Token).ConfigureAwait(true);

                if (result.Ok)
                {
                    _statusLine.Text = $"Joining {server.Endpoint}";
                    _activityLaunch.Text = result.Message;
                    SolarpunkLog.Info($"Connect succeeded: {result.Message} (pid={result.GamePid?.ToString() ?? "?"})");
                }
                else
                {
                    _statusLine.Text = "Launch failed";
                    _activityLaunch.Text = result.Message;
                    SolarpunkLog.Warn($"Connect failed: {result.Message}");
                }
            }
            catch (OperationCanceledException) when (connectCts.IsCancellationRequested)
            {
                SolarpunkLog.Info("Connect canceled");
                if (!_windowClosedCts.IsCancellationRequested)
                {
                    _statusLine.Text = "Launch canceled";
                    _activityLaunch.Text = "Launch canceled.";
                }
            }
            catch (Exception ex)
            {
                _statusLine.Text = "Launch failed";
                _activityLaunch.Text = $"Launch failed: {ex.Message}";
                SolarpunkLog.Error("Launch failed", ex);
            }
            finally
            {
                if (ReferenceEquals(_connectLaunchCts, connectCts))
                    _connectLaunchCts = null;
                ApplyConnectButtonState();
                if (!_windowClosedCts.IsCancellationRequested)
                    RefreshGameRunningState();
            }
        }
        finally
        {
            _connectInFlight = false;
        }
    }


    private async Task ConsoleConnectAsync()
    {
        try
        {
            _rcon?.Dispose();
            _rcon = new RconClient();
            var host = string.IsNullOrWhiteSpace(_consoleHost.Text) ? "127.0.0.1" : _consoleHost.Text!.Trim();
            if (!int.TryParse(_consolePort.Text, out var port))
                port = 7780;
            var password = _consolePassword.Text ?? "";
            AppendConsole($"$ connecting console to {host}:{port}", ConsoleTone.Prompt);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var ok = await _rcon.ConnectAndAuthAsync(host, port, password, cts.Token);
            if (!ok)
            {
                AppendConsole("auth failed", ConsoleTone.Error);
                _rcon.Dispose();
                _rcon = null;
                return;
            }

            if (_selected is not null && string.Equals(_selected.Host, host, StringComparison.OrdinalIgnoreCase))
            {
                _selected.AdminPassword = password;
                PersistServers();
            }
            AppendConsole("connected", ConsoleTone.Success);
            SetButtonContent(_consoleConnect, IconRefresh, "Reconnect");
        }
        catch (Exception ex)
        {
            AppendConsole($"connect error: {ex.Message}", ConsoleTone.Error);
        }
    }

    private async Task ConsoleSendAsync()
    {
        var command = _consoleInput.Text?.Trim();
        if (string.IsNullOrEmpty(command))
            return;

        if (_rcon is null || !_rcon.Authenticated)
        {
            await ConsoleConnectAsync();
            if (_rcon is null || !_rcon.Authenticated)
            {
                AppendConsole("not connected - save the server's admin password, then try again", ConsoleTone.Warning);
                return;
            }
        }

        _consoleHistory.Add(command);
        _consoleHistoryIndex = _consoleHistory.Count;
        AppendConsole($"> {command}", ConsoleTone.Prompt);
        _consoleInput.Text = "";

        try
        {
            var reply = await _rcon.ExecAsync(command);
            AppendConsole(string.IsNullOrEmpty(reply) ? "(empty)" : reply, ConsoleTone.Response);
        }
        catch (Exception ex)
        {
            AppendConsole($"send error: {ex.Message}", ConsoleTone.Error);
        }
    }

    private async Task HandleConsoleInputKeyAsync(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _consoleInput.IsDropDownOpen)
        {
            e.Handled = true;
            _consoleInput.IsDropDownOpen = false;
            return;
        }

        if (e.Key == Key.Tab)
        {
            e.Handled = CompleteConsoleCommand();
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (_consoleInput.IsDropDownOpen && CompleteConsoleCommand())
                return;

            await ConsoleSendAsync();
            return;
        }

        if (!_consoleInput.IsDropDownOpen && e.Key == Key.Up)
        {
            e.Handled = true;
            RecallConsoleHistory(-1);
            return;
        }

        if (!_consoleInput.IsDropDownOpen && e.Key == Key.Down)
        {
            e.Handled = true;
            RecallConsoleHistory(1);
        }
    }

    private bool CompleteConsoleCommand()
    {
        var typed = _consoleInput.Text?.Trim() ?? "";
        if (typed.Length == 0)
            return false;

        var match = _consoleInput.SelectedItem as string
            ?? ConsoleCommandNames.FirstOrDefault(command =>
                command.StartsWith(typed, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return false;

        _consoleInput.Text = match;
        _consoleInput.CaretIndex = match.Length;
        _consoleInput.SelectedItem = null;
        _consoleInput.IsDropDownOpen = false;
        return true;
    }

    private void RecallConsoleHistory(int delta)
    {
        if (_consoleHistory.Count == 0)
            return;

        _consoleHistoryIndex = Math.Clamp(_consoleHistoryIndex + delta, 0, _consoleHistory.Count);
        _consoleInput.Text = _consoleHistoryIndex >= _consoleHistory.Count ? "" : _consoleHistory[_consoleHistoryIndex];
        _consoleInput.CaretIndex = _consoleInput.Text?.Length ?? 0;
    }

    private void AppendConsole(string text, ConsoleTone tone)
    {
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            _consoleRows.Children.Add(new TextBlock
            {
                Text = line,
                Foreground = tone switch
                {
                    ConsoleTone.Prompt => Sun600,
                    ConsoleTone.Success => Sage500,
                    ConsoleTone.Warning => Coral500,
                    ConsoleTone.Error => Red500,
                    ConsoleTone.Muted => Ink500,
                    _ => Ink900,
                },
                FontFamily = ConsoleFont,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        Dispatcher.UIThread.Post(() => _consoleScroll.Offset = new Vector(_consoleScroll.Offset.X, double.MaxValue));
    }

    private void SetView(LauncherView view)
    {
        _serversView ??= BuildServersView();
        _consoleView ??= BuildConsoleView();
        _modsView ??= BuildModsView();

        _contentHost.Content = view switch
        {
            LauncherView.Servers => _serversView,
            LauncherView.Console => _consoleView,
            LauncherView.Mods => _modsView,
            _ => _serversView,
        };
        SetNavActive(_serversNav, view == LauncherView.Servers);
        SetNavActive(_consoleNav, view == LauncherView.Console);
        SetNavActive(_modsNav, view == LauncherView.Mods);

        // Lazy-load the manifest the first time the Mods view is shown for
        // the current server (or when the selection changed since last load).
        if (view == LauncherView.Mods)
            _ = RefreshModsViewAsync();
    }

    private static Button BuildNavButton(string icon, string text)
    {
        var button = new Button
        {
            Content = ButtonContent(icon, text),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Ink500,
            Padding = new Thickness(14, 8),
            MinHeight = 36,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        return button;
    }

    private static void SetNavActive(Button button, bool active)
    {
        button.Foreground = active ? Sun600 : Ink500;
        button.Background = active ? Card : Brushes.Transparent;
    }

    private static Button BuildToolButton(string text)
    {
        var button = new Button
        {
            Content = ButtonContent(IconFor(text), text),
            Background = Card,
            Foreground = Ink900,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 7),
            MinHeight = 34,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        return button;
    }

    private static Button BuildIconToolButton(string icon, string tooltip)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = icon,
                FontFamily = FluentIconFont,
                FontSize = 15,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            Width = 38,
            Height = 34,
            Background = Card,
            Foreground = Ink900,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        ToolTip.SetTip(button, tooltip);
        return button;
    }

    private static Button BuildPrimaryButton(string text)
    {
        var button = BuildToolButton(text);
        button.Background = Sun400;
        button.Foreground = InkOnSun;
        button.BorderBrush = Sun400;
        button.FontWeight = FontWeight.SemiBold;
        return button;
    }

    private static Button BuildWindowButton(string icon, string tooltip)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = icon,
                FontFamily = FluentIconFont,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            Width = 42,
            Height = 48,
            Background = Brushes.Transparent,
            Foreground = Ink500,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        ToolTip.SetTip(button, tooltip);
        return button;
    }

    private static Button BuildLinkButton(string text)
    {
        var button = new Button
        {
            Content = ButtonContent(IconExternal, text, 6),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Sun600,
            FontSize = 13,
            Padding = new Thickness(0),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        button.Classes.Add("link");
        return button;
    }

    private static object ButtonContent(string icon, string text, double spacing = 7)
    {
        return new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = spacing,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = icon,
                    FontFamily = FluentIconFont,
                    FontSize = 14,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                new TextBlock
                {
                    Text = text,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
    }

    private static void SetButtonContent(Button button, string icon, string text) =>
        button.Content = ButtonContent(icon, text);

    private static string IconFor(string text) =>
        text switch
        {
            "Add" => IconAdd,
            "Cancel" => IconCancel,
            "Characters" => IconCharacters,
            "Close" => IconClose,
            "Connect" => IconConnect,
            "Create" => IconAdd,
            "Refresh" => IconRefresh,
            "Reconnect" => IconRefresh,
            "Remove" => IconRemove,
            "Rent a Server" => IconHosting,
            "Save" => IconSave,
            "Send" => IconSend,
            "Use" => IconUse,
            "+ New character" => IconAdd,
            "New character" => IconAdd,
            _ => IconExternal,
        };

    private static TextBox BuildTextBox(string watermark)
    {
        return new TextBox
        {
            Watermark = watermark,
            Background = Card,
            Foreground = Ink900,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 7),
            MinHeight = 34,
        };
    }

    private static AutoCompleteBox BuildCommandBox()
    {
        return new AutoCompleteBox
        {
            Watermark = "command",
            ItemsSource = ConsoleCommandNames,
            FilterMode = AutoCompleteFilterMode.StartsWith,
            MinimumPrefixLength = 1,
            MinimumPopulateDelay = TimeSpan.Zero,
            MaxDropDownHeight = 180,
            IsTextCompletionEnabled = false,
            Background = Card,
            Foreground = Ink900,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 7),
            MinHeight = 34,
            FontFamily = ConsoleFont,
        };
    }

    private static Border Panel(Control child)
    {
        return new Border
        {
            Background = Cream50,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = child,
        };
    }

    private static IBrush SignalBrush(string signal) =>
        signal == "Checking" || signal == "Pinging" ? Sun400 :
        signal == "Online (outdated)" ? Coral500 :
        signal == "Online" ? Sage500 :
        Red500;

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    private enum LauncherView
    {
        Servers,
        Console,
        Mods,
    }

    private enum ConsoleTone
    {
        Prompt,
        Response,
        Success,
        Warning,
        Error,
        Muted,
    }

    /// <summary>
    /// Modal join-password prompt for v0.3.56. shown by ConnectAsync
    /// when the A2S query says the server is password-protected but the
    /// saved entry has none. Result is the raw text (byte-exact, NOT
    /// trimmed — BeaconAuth compares byte-exact server-side, so trimming
    /// here would silently break passwords with leading/trailing
    /// spaces). Null = canceled.
    /// </summary>
    // Open the per-server character roster. Picking an entry fills the
    // character box; the actual identity write happens at Connect time.
    private async Task ShowCharacterPickerAsync()
    {
        if (_selected is null)
        {
            _statusLine.Text = "Select a server first";
            return;
        }
        var dialog = new CharacterPickerDialog(_selected.Endpoint);
        var picked = await dialog.ShowDialog<string?>(this);
        if (!string.IsNullOrWhiteSpace(picked) && _characterNameBox is not null)
        {
            _characterNameBox.Text = picked;
            _statusLine.Text = $"Using {picked}";
        }
    }

    /// <summary>
    /// Per-server character roster picker (CharacterStore-backed). Lists every
    /// character played on this endpoint, newest-played first, plus a "new
    /// character" name box. Returns the chosen name or null on cancel.
    /// </summary>
    private sealed class CharacterPickerDialog : Window
    {
        public CharacterPickerDialog(string endpoint)
        {
            Title = "Characters";
            Width = 460;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ApplyChromelessDialog(this);
            Background = Cream100;
            ApplyWindowIcon(this);

            var layout = new StackPanel { Spacing = 12, Margin = new Thickness(20) };
            layout.Children.Add(DialogTitle("Characters"));

            var roster = CharacterStore.ForServer(endpoint)
                .OrderByDescending(c => c.LastPlayed ?? c.Created)
                .ToList();

            if (roster.Count == 0)
            {
                layout.Children.Add(new TextBlock
                {
                    Text = "No characters yet",
                    Foreground = Ink500,
                    FontSize = 13,
                });
            }
            else
            {
                var list = new StackPanel { Spacing = 6 };
                foreach (var c in roster)
                {
                    var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                    var meta = c.LastPlayed is { } lp
                        ? $"Last played {lp.ToLocalTime():g}"
                        : "Never played";
                    var label = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
                    label.Children.Add(new TextBlock
                    {
                        Text = c.Name,
                        Foreground = Ink900,
                        FontSize = 14,
                        FontWeight = FontWeight.SemiBold,
                    });
                    label.Children.Add(new TextBlock
                    {
                        Text = meta,
                        Foreground = Ink500,
                        FontSize = 11,
                    });
                    Grid.SetColumn(label, 0);
                    row.Children.Add(label);

                    var use = BuildToolButton("Use");
                    use.VerticalAlignment = VerticalAlignment.Center;
                    var name = c.Name;
                    use.Click += (_, _) => Close(name);
                    Grid.SetColumn(use, 1);
                    row.Children.Add(use);

                    list.Children.Add(row);
                }
                var scroll = new ScrollViewer
                {
                    Content = list,
                    MaxHeight = 260,
                    VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                };
                layout.Children.Add(scroll);
            }

            var newBox = BuildTextBox("Character name");
            layout.Children.Add(DialogField("New character", newBox));
            var error = new TextBlock
            {
                Foreground = Red500,
                FontSize = 12,
                MinHeight = 16,
                TextWrapping = TextWrapping.Wrap,
            };
            layout.Children.Add(error);

            var buttons = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
                Margin = new Thickness(0, 6, 0, 0),
            };
            var cancel = BuildToolButton("Cancel");
            cancel.Click += (_, _) => Close(null);
            Grid.SetColumn(cancel, 1);
            buttons.Children.Add(cancel);
            var create = BuildPrimaryButton("Create");
            create.Margin = new Thickness(8, 0, 0, 0);
            create.Click += (_, _) =>
            {
                var v = Services.ConnectTargetService.SanitizeCharacterName(newBox.Text);
                if (string.IsNullOrEmpty(v)) { error.Text = "Character name is required."; return; }
                Close(v);
            };
            Grid.SetColumn(create, 2);
            buttons.Children.Add(create);
            layout.Children.Add(buttons);

            Content = Panel(layout);
        }
    }

    private sealed class JoinPasswordPromptDialog : Window
    {
        private readonly TextBox _pw;
        private readonly TextBlock _error;

        public JoinPasswordPromptDialog(string endpoint)
        {
            Title = "Server password";
            Width = 420;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ApplyChromelessDialog(this);
            Background = Cream100;
            ApplyWindowIcon(this);

            var layout = new StackPanel { Spacing = 12, Margin = new Thickness(20) };
            layout.Children.Add(DialogTitle("Server requires a password"));
            layout.Children.Add(new TextBlock
            {
                Text = $"{endpoint} is password-protected. Enter the join password to continue. " +
                       "It's stored locally for next time so you don't have to type it again.",
                Foreground = Ink500,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });
            _pw = BuildTextBox("Server password");
            _pw.PasswordChar = '•';
            layout.Children.Add(DialogField("Password", _pw));
            _error = new TextBlock
            {
                Foreground = Red500,
                FontSize = 12,
                MinHeight = 16,
                TextWrapping = TextWrapping.Wrap,
            };
            layout.Children.Add(_error);

            var buttons = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
                Margin = new Thickness(0, 6, 0, 0),
            };
            var cancel = BuildToolButton("Cancel");
            cancel.Click += (_, _) => Close(null);
            Grid.SetColumn(cancel, 1);
            buttons.Children.Add(cancel);
            var ok = BuildPrimaryButton("Connect");
            ok.Margin = new Thickness(8, 0, 0, 0);
            ok.Click += (_, _) =>
            {
                // Read .Text directly — no .Trim() — see class doc-comment
                // for the byte-exact reasoning.
                var v = _pw.Text ?? "";
                if (string.IsNullOrEmpty(v)) { _error.Text = "Password is required."; return; }
                Close(v);
            };
            Grid.SetColumn(ok, 2);
            buttons.Children.Add(ok);
            layout.Children.Add(buttons);

            Content = Panel(layout);
        }
    }

    private sealed class AdminPasswordPromptDialog : Window
    {
        private readonly TextBox _pw;
        private readonly TextBlock _error;

        public AdminPasswordPromptDialog(string endpoint)
        {
            Title = "Admin password";
            Width = 420;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ApplyChromelessDialog(this);
            Background = Cream100;
            ApplyWindowIcon(this);

            var layout = new StackPanel { Spacing = 12, Margin = new Thickness(20) };
            layout.Children.Add(DialogTitle("Admin password"));
            layout.Children.Add(new TextBlock
            {
                Text = $"World transfer commands run against {endpoint} using the server's admin (RCON) password. " +
                       "It's stored locally and never shown again.",
                Foreground = Ink500,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });
            _pw = BuildTextBox("Admin password");
            _pw.PasswordChar = '•';
            layout.Children.Add(DialogField("Password", _pw));
            _error = new TextBlock
            {
                Foreground = Red500,
                FontSize = 12,
                MinHeight = 16,
                TextWrapping = TextWrapping.Wrap,
            };
            layout.Children.Add(_error);

            var buttons = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
                Margin = new Thickness(0, 6, 0, 0),
            };
            var cancel = BuildToolButton("Cancel");
            cancel.Click += (_, _) => Close(null);
            Grid.SetColumn(cancel, 1);
            buttons.Children.Add(cancel);
            var ok = BuildPrimaryButton("Continue");
            ok.Margin = new Thickness(8, 0, 0, 0);
            ok.Click += (_, _) =>
            {
                var v = (_pw.Text ?? "").Trim();
                if (string.IsNullOrEmpty(v)) { _error.Text = "Password is required."; return; }
                Close(v);
            };
            Grid.SetColumn(ok, 2);
            buttons.Children.Add(ok);
            layout.Children.Add(buttons);

            Content = Panel(layout);
        }
    }

    /// <summary>
    /// Minimal yes/no confirmation popup. Returns true on the confirm
    /// button, false on cancel or close.
    /// </summary>
    private sealed class ConfirmDialog : Window
    {
        public ConfirmDialog(string title, string message, string confirmLabel, string cancelLabel)
        {
            Title = title;
            Width = 420;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ApplyChromelessDialog(this);
            Background = Cream100;
            ApplyWindowIcon(this);

            var layout = new StackPanel { Spacing = 14, Margin = new Thickness(20) };
            layout.Children.Add(DialogTitle(title));
            layout.Children.Add(new TextBlock
            {
                Text = message,
                Foreground = Ink500,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });
            var buttons = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
                Margin = new Thickness(0, 6, 0, 0),
            };
            var cancel = BuildToolButton(cancelLabel);
            cancel.Click += (_, _) => Close(false);
            Grid.SetColumn(cancel, 1);
            buttons.Children.Add(cancel);
            var confirm = BuildPrimaryButton(confirmLabel);
            confirm.Margin = new Thickness(8, 0, 0, 0);
            confirm.Click += (_, _) => Close(true);
            Grid.SetColumn(confirm, 2);
            buttons.Children.Add(confirm);
            layout.Children.Add(buttons);
            Content = Panel(layout);
        }
    }

    private sealed class WorldTransferDialog : Window
    {
        private readonly ServerEntry _server;
        private readonly List<ServerEntry> _allServers;
        private readonly Action _persistServersCallback;
        private SolarpunkHttpClient _client;
        private TextBlock _status = null!;
        private ListBox _snapshotList = null!;
        private TextBox _adminPwBox = null!;
        private Button _adminPwSave = null!;
        private TextBlock _adminPwHint = null!;
        private Panel _contentPanel = null!;

        public WorldTransferDialog(ServerEntry server, List<ServerEntry> allServers, Action persistServersCallback)
        {
            _server = server;
            _allServers = allServers;
            _persistServersCallback = persistServersCallback;
            _client = new SolarpunkHttpClient(server.Host, server.HttpPort, server.AdminPassword ?? "");

            Title = "World backups";
            Width = 620;
            MinWidth = 580;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ApplyChromelessDialog(this);
            Background = Cream100;
            ApplyWindowIcon(this);

            var layout = new StackPanel { Spacing = 14, Margin = new Thickness(20) };
            layout.Children.Add(DialogTitle($"World backups — {server.DisplayName}"));
            layout.Children.Add(new TextBlock
            {
                Text = "Take snapshots, restore an earlier world, or import a save for this server. " +
                       "Restore and import actions temporarily stop the world while the files are swapped.",
                Foreground = Ink500,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });

            // === Inline auth section ===
            // Always visible. If a password is already saved we show a
            // muted "(saved)" hint with a Clear button. If not, we show an
            // input + Save button. No nested modal — that was the cause of
            // the earlier focus-loss freeze.
            layout.Children.Add(SectionHeader("Admin password"));
            layout.Children.Add(new TextBlock
            {
                Text = "Same as the server's RCON/admin password. Solarpunk stores it only on this machine " +
                       "and uses it for snapshots, restores, imports, and the Console tab.",
                Foreground = Ink500,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });
            var authRow = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            };
            _adminPwBox = BuildTextBox("Admin password");
            _adminPwBox.PasswordChar = '•';
            _adminPwBox.Text = "";
            Grid.SetColumn(_adminPwBox, 0);
            authRow.Children.Add(_adminPwBox);
            _adminPwSave = BuildPrimaryButton("Save");
            _adminPwSave.Margin = new Thickness(8, 0, 0, 0);
            _adminPwSave.Click += (_, _) => SaveAdminPassword();
            Grid.SetColumn(_adminPwSave, 1);
            authRow.Children.Add(_adminPwSave);
            layout.Children.Add(authRow);
            _adminPwHint = new TextBlock
            {
                Foreground = Ink500,
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            layout.Children.Add(_adminPwHint);

            _contentPanel = new StackPanel { Spacing = 14 };
            _status = new TextBlock
            {
                Foreground = Ink500,
                FontSize = 12,
                Margin = new Thickness(0, 6, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            layout.Children.Add(_status);

            var bottom = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Margin = new Thickness(0, 14, 0, 0),
            };
            var close = BuildToolButton("Close");
            close.Click += (_, _) => Close(null);
            Grid.SetColumn(close, 1);
            bottom.Children.Add(close);
            layout.Children.Add(bottom);

            Content = Panel(new ScrollViewer { Content = layout, MaxHeight = 720 });

            BuildContentPanel(_contentPanel);
            UpdateAuthState();

            Opened += async (_, _) =>
            {
                if (!string.IsNullOrEmpty(_server.AdminPassword))
                    await RefreshSnapshotsAsync();
            };
        }

        private void BuildContentPanel(Panel panel)
        {
            panel.Children.Clear();

            panel.Children.Add(SectionHeader("Snapshots"));
            _snapshotList = new ListBox
            {
                Background = Card,
                Foreground = Ink900,
                BorderBrush = Line,
                BorderThickness = new Thickness(1),
                Height = 160,
                ItemTemplate = new FuncDataTemplate<SolarpunkHttpClient.SnapshotSummary>((s, _) =>
                    new TextBlock
                    {
                        Text = s is null ? "" :
                               $"{DateTimeOffset.FromUnixTimeSeconds(s.TakenUnix):yyyy-MM-dd HH:mm}  " +
                               $"({s.SizeBytes / 1024:N0} KB)  {s.Id[..Math.Min(16, s.Id.Length)]}",
                        Foreground = Ink900,
                        Padding = new Thickness(8, 4),
                    }, true),
            };
            panel.Children.Add(_snapshotList);

            var snapRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
            var refreshBtn = BuildToolButton("Refresh");
            refreshBtn.Click += async (_, _) => await RefreshSnapshotsAsync();
            snapRow.Children.Add(refreshBtn);
            var downloadBtn = BuildToolButton("Download");
            downloadBtn.Click += async (_, _) => await DownloadSelectedAsync();
            snapRow.Children.Add(downloadBtn);
            var restoreBtn = BuildToolButton("Restore");
            restoreBtn.Foreground = Coral500;
            restoreBtn.BorderBrush = Coral500;
            restoreBtn.Click += async (_, _) => await RestoreSelectedAsync();
            snapRow.Children.Add(restoreBtn);
            panel.Children.Add(snapRow);

            panel.Children.Add(SectionHeader("Import vanilla save"));
            panel.Children.Add(new TextBlock
            {
                Text = "Upload a local Solarpunk vanilla save folder (or any saved Solarpunk snapshot zip) " +
                       "and use it as this server's active world. A pre-import backup is taken automatically.",
                Foreground = Ink500,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });
            var importBtn = BuildToolButton("Choose file & import");
            importBtn.Foreground = Sun600;
            importBtn.BorderBrush = Sun400;
            importBtn.HorizontalAlignment = HorizontalAlignment.Left;
            importBtn.Click += async (_, _) => await ImportFileAsync();
            panel.Children.Add(importBtn);

            var others = _allServers.Where(s => !ReferenceEquals(s, _server)).ToList();
            if (others.Count > 0)
            {
                panel.Children.Add(SectionHeader("Migrate from another server"));
                var sourceBox = new ComboBox
                {
                    PlaceholderText = "Pick source server",
                    Background = Card,
                    Foreground = Ink900,
                    BorderBrush = Line,
                    BorderThickness = new Thickness(1),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    ItemsSource = others,
                    ItemTemplate = new FuncDataTemplate<ServerEntry>((s, _) =>
                        new TextBlock { Text = s is null ? "" : (s.DisplayName + " (" + s.Endpoint + ")"), Foreground = Ink900, Padding = new Thickness(8, 4) }, true),
                };
                panel.Children.Add(sourceBox);
                var migrateBtn = BuildToolButton("Migrate latest snapshot");
                migrateBtn.HorizontalAlignment = HorizontalAlignment.Left;
                migrateBtn.Click += async (_, _) =>
                {
                    if (sourceBox.SelectedItem is ServerEntry src)
                        await MigrateFromAsync(src);
                };
                panel.Children.Add(migrateBtn);
            }
        }

        private void UpdateAuthState()
        {
            var hasPw = !string.IsNullOrEmpty(_server.AdminPassword);
            if (hasPw)
            {
                _adminPwBox.IsEnabled = false;
                // Drop the PasswordChar mask so the "(saved)" placeholder
                // is human-readable instead of rendered as more bullets.
                _adminPwBox.PasswordChar = '\0';
                _adminPwBox.Text = "(saved)";
                _adminPwSave.Content = "Clear";
                _adminPwHint.Foreground = Sage500;
                _adminPwHint.Text = "Saved. Click Clear to re-enter.";
                _contentPanel.IsVisible = true;
            }
            else
            {
                _adminPwBox.IsEnabled = true;
                _adminPwBox.PasswordChar = '•';
                _adminPwBox.Text = "";
                _adminPwSave.Content = "Save";
                _adminPwHint.Foreground = Ink500;
                _adminPwHint.Text = "Enter the server's admin password to enable snapshot actions.";
                _contentPanel.IsVisible = false;
            }
        }

        private void SaveAdminPassword()
        {
            // Save / Clear toggle. When a password is saved the button reads
            // "Clear" and resets the field; otherwise it stores what the
            // customer typed and refreshes the snapshot list.
            if (!string.IsNullOrEmpty(_server.AdminPassword))
            {
                _server.AdminPassword = null;
                _persistServersCallback();
                _client.Dispose();
                _client = new SolarpunkHttpClient(_server.Host, _server.HttpPort, "");
                UpdateAuthState();
                _status.Text = "";
                _snapshotList.ItemsSource = null;
                return;
            }

            var pw = (_adminPwBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(pw))
            {
                _adminPwHint.Foreground = Red500;
                _adminPwHint.Text = "Password is required.";
                return;
            }
            _server.AdminPassword = pw;
            _persistServersCallback();
            _client.Dispose();
            _client = new SolarpunkHttpClient(_server.Host, _server.HttpPort, pw);
            UpdateAuthState();
            _ = RefreshSnapshotsAsync();
        }

        private async Task RefreshSnapshotsAsync()
        {
            _status.Foreground = Ink500;
            _status.Text = "Loading snapshots";
            try
            {
                var list = await _client.ListSnapshotsAsync();
                if (list is null)
                {
                    _status.Foreground = Coral500;
                    _status.Text = "Server did not respond. Check the admin password and HTTP port.";
                    _snapshotList.ItemsSource = null;
                    return;
                }
                _snapshotList.ItemsSource = list.Snapshots;
                _status.Foreground = Sage500;
                _status.Text = $"{list.Snapshots.Count} snapshot(s).";
            }
            catch (Exception ex)
            {
                _status.Foreground = Coral500;
                _status.Text = $"Could not load snapshots: {ex.Message}";
            }
        }

        private async Task DownloadSelectedAsync()
        {
            if (_snapshotList.SelectedItem is not SolarpunkHttpClient.SnapshotSummary snap)
            {
                _status.Text = "Select a snapshot first.";
                return;
            }
            var file = await StorageProvider.SaveFilePickerAsync(
                new Avalonia.Platform.Storage.FilePickerSaveOptions
                {
                    Title = "Save snapshot",
                    DefaultExtension = "zip",
                    SuggestedFileName = $"sundial-{_server.Label.Replace(' ', '_')}-{snap.Id[..16]}.zip",
                });
            if (file is null) return;
            var dest = file.Path.LocalPath;

            _status.Foreground = Sun600;
            _status.Text = $"Downloading {snap.Id[..12]}";
            var sha = await _client.DownloadSnapshotAsync(snap.Id, dest);
            if (sha is null)
            {
                _status.Foreground = Coral500;
                _status.Text = "Download failed.";
                return;
            }
            _status.Foreground = Sage500;
            _status.Text = $"Saved to {dest}";
        }

        private async Task RestoreSelectedAsync()
        {
            if (_snapshotList.SelectedItem is not SolarpunkHttpClient.SnapshotSummary snap)
            {
                _status.Text = "Select a snapshot first.";
                return;
            }
            _status.Foreground = Sun600;
            _status.Text = $"Restoring {snap.Id[..12]}, server will restart";
            var ok = await _client.RestoreSnapshotAsync(snap.Id);
            _status.Foreground = ok ? Sage500 : Coral500;
            _status.Text = ok ? "Restored. Server is restarting." : "Restore failed.";
            if (ok) await RefreshSnapshotsAsync();
        }

        private async Task ImportFileAsync()
        {
            var files = await StorageProvider.OpenFilePickerAsync(
                new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = "Choose vanilla save / snapshot zip",
                    AllowMultiple = false,
                    FileTypeFilter = new List<Avalonia.Platform.Storage.FilePickerFileType>
                    {
                        new("Zip archive") { Patterns = new[] { "*.zip" } },
                    },
                });
            if (files is null || files.Count == 0) return;
            var path = files[0].Path.LocalPath;

            _status.Foreground = Sun600;
            _status.Text = $"Uploading {Path.GetFileName(path)} and restoring";
            var ok = await _client.ImportAndRestoreAsync(path);
            _status.Foreground = ok ? Sage500 : Coral500;
            _status.Text = ok ? "Imported. Server is restarting with the new world." : "Import failed.";
            if (ok) await RefreshSnapshotsAsync();
        }

        private async Task MigrateFromAsync(ServerEntry source)
        {
            if (string.IsNullOrEmpty(source.AdminPassword))
            {
                _status.Foreground = Coral500;
                _status.Text = $"No admin password saved for {source.Endpoint}. Open World on that server first.";
                return;
            }

            _status.Foreground = Sun600;
            _status.Text = $"Reading snapshots from {source.Endpoint}";

            using var srcClient = new SolarpunkHttpClient(source.Host, source.HttpPort, source.AdminPassword);
            var list = await srcClient.ListSnapshotsAsync();
            if (list is null || list.Snapshots.Count == 0)
            {
                _status.Foreground = Coral500;
                _status.Text = $"{source.Endpoint} has no snapshots.";
                return;
            }
            var newest = list.Snapshots.OrderByDescending(s => s.TakenUnix).First();
            var temp = Path.Combine(Path.GetTempPath(), $"sundial-migrate-{Guid.NewGuid():N}.zip");
            _status.Text = $"Downloading {newest.Id[..12]} from source";
            var sha = await srcClient.DownloadSnapshotAsync(newest.Id, temp);
            if (sha is null)
            {
                _status.Foreground = Coral500;
                _status.Text = "Source download failed.";
                return;
            }
            _status.Text = "Uploading + restoring on target";
            var ok = await _client.ImportAndRestoreAsync(temp);
            try { File.Delete(temp); } catch { }
            _status.Foreground = ok ? Sage500 : Coral500;
            _status.Text = ok
                ? $"Migrated {newest.Id[..12]} from {source.Endpoint}. Target is restarting."
                : "Restore failed on target.";
            if (ok) await RefreshSnapshotsAsync();
        }
    }

    private sealed class AddServerDialog : Window
    {
        private readonly TextBox _label;
        private readonly TextBox _host;
        private readonly TextBox _port;
        private readonly TextBox _password;
        private readonly TextBlock _error;

        public AddServerDialog()
        {
            Title = "Add Server";
            Width = 420;
            Height = 540;
            MinWidth = 420;
            MinHeight = 540;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ApplyChromelessDialog(this);
            Background = Cream100;
            ApplyWindowIcon(this);

            var layout = new StackPanel
            {
                Spacing = 10,
                Margin = new Thickness(18),
            };
            layout.Children.Add(DialogTitle("Add server"));

            _label = BuildTextBox("");
            _host = BuildTextBox("");
            _port = BuildTextBox("");
            _password = BuildTextBox("");
            // Password shown as plaintext by default — Add Server is a
            // one-time setup screen, not a sign-in form. No reason to
            // obscure a value the user just typed and needs to verify.

            layout.Children.Add(DialogField("Name *", _label));
            layout.Children.Add(DialogField("IP / Host *", _host));
            layout.Children.Add(DialogField("Port *", _port));
            layout.Children.Add(DialogField("Server password", _password));

            _error = new TextBlock
            {
                Foreground = Red500,
                FontSize = 12,
                MinHeight = 18,
            };
            layout.Children.Add(_error);

            var buttons = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            };
            var cancel = BuildToolButton("Cancel");
            cancel.Click += (_, _) => Close(null);
            Grid.SetColumn(cancel, 1);
            buttons.Children.Add(cancel);

            var add = BuildPrimaryButton("Add");
            add.Margin = new Thickness(8, 0, 0, 0);
            add.Click += (_, _) => Save();
            Grid.SetColumn(add, 2);
            buttons.Children.Add(add);
            layout.Children.Add(buttons);

            Content = Panel(layout);
        }

        private void Save()
        {
            var host = _host.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(host))
            {
                _error.Text = "Host is required.";
                return;
            }

            // Cap at 65531, not 65535 — Solarpunk derives RCON (gameplay+3),
            // HTTP admin API (gameplay+4) and other adjacent ports off the
            // gameplay port. Allowing 65532–65535 would let those derived
            // ports overflow past 65535 and silently fail to bind.
            if (!int.TryParse(_port.Text, out var port) || port <= 0 || port > 65531)
            {
                _error.Text = "Port must be between 1 and 65531.";
                return;
            }

            var label = string.IsNullOrWhiteSpace(_label.Text) ? $"{host}:{port}" : _label.Text!.Trim();
            // Empty string normalizes to null so ServerEntry.Password reads
            // as "no password" rather than "empty password" downstream.
            var password = string.IsNullOrEmpty(_password.Text) ? null : _password.Text;
            Close(new ServerEntry(label, host, port, password: password));
        }
    }

    /// <summary>
    /// Edit-server dialog. Mirrors AddServerDialog's layout but pre-fills
    /// from an existing ServerEntry and returns an EditResult struct so the
    /// caller can detect host/port changes (those need a re-ping and
    /// IsPasswordProtected reset). Server password field is plaintext-by-
    /// default, same reasoning as Add: this is a deliberate edit screen,
    /// not a sign-in form, and obscuring the value the customer just
    /// pasted in only makes it harder to verify they typed it right.
    /// </summary>
    private sealed class EditServerDialog : Window
    {
        public sealed record EditResult(string Label, string Host, int Port, string? Password);

        private readonly TextBox _label;
        private readonly TextBox _host;
        private readonly TextBox _port;
        private readonly TextBox _password;
        private readonly TextBlock _error;

        public EditServerDialog(ServerEntry existing)
        {
            Title = "Edit Server";
            Width = 420;
            Height = 540;
            MinWidth = 420;
            MinHeight = 540;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ApplyChromelessDialog(this);
            Background = Cream100;
            ApplyWindowIcon(this);

            var layout = new StackPanel
            {
                Spacing = 10,
                Margin = new Thickness(18),
            };
            layout.Children.Add(DialogTitle("Edit server"));

            _label = BuildTextBox("");
            _label.Text = existing.Label;
            _host = BuildTextBox("");
            _host.Text = existing.Host;
            _port = BuildTextBox("");
            _port.Text = existing.Port.ToString();
            _password = BuildTextBox("");
            _password.Text = existing.Password ?? "";

            layout.Children.Add(DialogField("Name *", _label));
            layout.Children.Add(DialogField("IP / Host *", _host));
            layout.Children.Add(DialogField("Port *", _port));
            layout.Children.Add(DialogField("Server password", _password));

            _error = new TextBlock
            {
                Foreground = Red500,
                FontSize = 12,
                MinHeight = 18,
            };
            layout.Children.Add(_error);

            var buttons = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            };
            var cancel = BuildToolButton("Cancel");
            cancel.Click += (_, _) => Close(null);
            Grid.SetColumn(cancel, 1);
            buttons.Children.Add(cancel);

            var save = BuildPrimaryButton("Save");
            save.Margin = new Thickness(8, 0, 0, 0);
            save.Click += (_, _) => Save();
            Grid.SetColumn(save, 2);
            buttons.Children.Add(save);
            layout.Children.Add(buttons);

            Content = Panel(layout);
        }

        private void Save()
        {
            var host = _host.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(host))
            {
                _error.Text = "Host is required.";
                return;
            }

            // Same 65531 cap as Add Server: Solarpunk derives RCON / HTTP /
            // query ports off the gameplay port (+2 / +3 / +4), so a
            // gameplay port above 65531 would push those past 65535.
            if (!int.TryParse(_port.Text, out var port) || port <= 0 || port > 65531)
            {
                _error.Text = "Port must be between 1 and 65531.";
                return;
            }

            var label = string.IsNullOrWhiteSpace(_label.Text) ? $"{host}:{port}" : _label.Text!.Trim();
            // Server password is read byte-exact (NOT trimmed) — BeaconAuth
            // compares byte-exact on the server side, and trimming here
            // would silently break passwords with leading/trailing spaces
            // that the panel did not strip. Empty string normalizes to null
            // to mean "no password" rather than "empty string password".
            var password = string.IsNullOrEmpty(_password.Text) ? null : _password.Text;
            Close(new EditResult(label, host, port, password));
        }
    }

    private sealed class SettingsDialog : Window
    {
        private readonly TextBox _gamePath;
        private readonly TextBox _logs;
        private readonly CheckBox _startupCheck;
        private readonly CheckBox _updateCheck;
        private readonly TextBlock _updateStatus;
        private readonly string? _defaultLogDirectory;

        public SettingsDialog(LauncherSettings settings)
        {
            _defaultLogDirectory = settings.LogDirectory is null ? settings.EffectiveLogDirectory : null;

            Title = "Settings";
            Width = 640;
            MinWidth = 600;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ApplyChromelessDialog(this);
            Background = Cream100;
            ApplyWindowIcon(this);

            var layout = new StackPanel
            {
                Spacing = 14,
                Margin = new Thickness(20),
            };
            layout.Children.Add(DialogTitle("Settings"));

            // === Paths section ===
            layout.Children.Add(SectionHeader("Game path"));
            layout.Children.Add(new TextBlock
            {
                Text = "Leave blank to auto-detect your Solarpunk install (Steam / Epic / MS Store). " +
                       "Set this only if the scan misses your install — point it at the Solarpunk folder or exe.",
                Foreground = Ink500,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });

            _gamePath = BuildTextBox("");
            _gamePath.Text = settings.GamePathOverride ?? "";
            _logs = BuildTextBox("");
            _logs.Text = settings.LogDirectory ?? settings.EffectiveLogDirectory;

            layout.Children.Add(DialogField("Solarpunk (optional override)", _gamePath));
            layout.Children.Add(DialogField("Log directory", _logs));

            // === Startup section ===
            layout.Children.Add(SectionHeader("Startup"));
            _startupCheck = new CheckBox
            {
                Content = "Start Solarpunk when Windows starts",
                IsChecked = settings.StartOnWindowsLogin || StartupRegistration.IsEnabled(),
                Foreground = Ink900,
            };
            layout.Children.Add(_startupCheck);
            _updateCheck = new CheckBox
            {
                Content = "Install updates automatically",
                IsChecked = settings.CheckForUpdatesOnStart,
                Foreground = Ink900,
            };
            layout.Children.Add(_updateCheck);

            // === Updates section ===
            layout.Children.Add(SectionHeader("Updates"));
            var versionRow = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 8,
            };
            versionRow.Children.Add(new TextBlock
            {
                Text = "Current version:",
                Foreground = Ink500,
                VerticalAlignment = VerticalAlignment.Center,
            });
            versionRow.Children.Add(new TextBlock
            {
                Text = SolarpunkLifecycle.CurrentVersion,
                Foreground = Ink900,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            layout.Children.Add(versionRow);

            var checkUpdatesBtn = BuildToolButton("Check for updates");
            checkUpdatesBtn.HorizontalAlignment = HorizontalAlignment.Left;
            _updateStatus = new TextBlock
            {
                Text = "",
                Foreground = Ink500,
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            checkUpdatesBtn.Click += async (_, _) => await CheckForUpdatesAsync();
            layout.Children.Add(checkUpdatesBtn);
            layout.Children.Add(_updateStatus);

            // (Uninstall is handled by Windows itself — the NSIS installer
            // registers an uninstaller in Settings → Apps & features and
            // adds a Start Menu shortcut. No duplicate button in here.)

            // === Save / Cancel ===
            var buttons = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
                Margin = new Thickness(0, 14, 0, 0),
            };
            var cancel = BuildToolButton("Cancel");
            cancel.Click += (_, _) => Close(null);
            Grid.SetColumn(cancel, 1);
            buttons.Children.Add(cancel);

            var save = BuildPrimaryButton("Save");
            save.Margin = new Thickness(8, 0, 0, 0);
            save.Click += (_, _) => Save();
            Grid.SetColumn(save, 2);
            buttons.Children.Add(save);
            layout.Children.Add(buttons);

            Content = Panel(layout);
        }

        private async Task CheckForUpdatesAsync()
        {
            _updateStatus.Foreground = Ink500;
            _updateStatus.Text = "Checking";

            // Route through the MainWindow's unified staging path so that
            // _stagedUpdateVersion is set in the same place no matter
            // which trigger (background poll, 4-hour timer, this button)
            // fired. Earlier versions had a parallel call directly to
            // AutoUpdater.DownloadAndStageAsync here which (a) didn't set
            // _stagedUpdateVersion, so the next background poll could
            // re-download the same version, and (b) could race the
            // background poll on app.pending/. _stagingGate on MainWindow
            // closes both holes.
            var main = this.Owner as MainWindow;
            if (main is null)
            {
                _updateStatus.Foreground = Coral500;
                _updateStatus.Text = "Internal error: settings dialog has no owner window.";
                return;
            }

            var outcome = await main.StageUpdateOnceAsync();
            if (outcome.Error is not null && !outcome.Staged)
            {
                _updateStatus.Foreground = Coral500;
                _updateStatus.Text = outcome.UpdateAvailable
                    ? $"Download failed: {outcome.Error}"
                    : $"Update check failed: {outcome.Error}";
                return;
            }
            if (!outcome.UpdateAvailable)
            {
                _updateStatus.Foreground = Sage500;
                _updateStatus.Text = $"You're up to date (current: {SolarpunkLifecycle.CurrentVersion}).";
                return;
            }
            if (outcome.SideLoadedNoAutoApply)
            {
                _updateStatus.Foreground = Coral500;
                _updateStatus.Text = $"v{outcome.Version} available, but auto-install requires a real install (this looks like a side-loaded build).";
                return;
            }
            if (!outcome.Staged)
            {
                _updateStatus.Foreground = Coral500;
                _updateStatus.Text = $"Download failed: {outcome.Error}";
                return;
            }

            _updateStatus.Foreground = Sage500;
            _updateStatus.Text = $"Restarting Solarpunk to apply v{outcome.Version}";

            // Also surface on the main launcher so the user sees the restart
            // banner even if they close the Settings dialog before the 2s
            // delay elapses.
            main._statusLine.Text = $"Restarting Solarpunk to apply v{outcome.Version}";
            main._activityLaunch.Text = $"Restarting in 2s to apply v{outcome.Version}";

            if (main.IsGameRunningForUpdateGuard())
            {
                _updateStatus.Text = $"v{outcome.Version} is ready. Quit Solarpunk to finish the update.";
                main._statusLine.Text = $"Update ready: v{outcome.Version}";
                main._activityLaunch.Text = "Quit Solarpunk to finish the Solarpunk update.";
                return;
            }

            // Auto-apply: short grace period so the user can read the status,
            // then close Settings AND the main launcher window. The Closed
            // handler runs SolarpunkUpdater.cmd to swap app.pending → app and
            // relaunch. Even if the user closes Settings manually before the
            // delay, the main-window status is already set and the close
            // path below still fires.
            await Task.Delay(TimeSpan.FromSeconds(2));
            try { Close(); } catch { }
            main.RequestRealExit();
        }

        private void Save()
        {
            Close(new LauncherSettings
            {
                GamePathOverride = OverrideValue(_gamePath.Text, null),
                LogDirectory = OverrideValue(_logs.Text, _defaultLogDirectory),
                StartOnWindowsLogin = _startupCheck.IsChecked == true,
                CheckForUpdatesOnStart = _updateCheck.IsChecked == true,
            });
        }
    }

    private static TextBlock SectionHeader(string text) => new()
    {
        Text = text,
        Foreground = Sun600,
        FontSize = 13,
        FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(0, 8, 0, 0),
    };

    private sealed class AboutDialog : Window
    {
        public AboutDialog()
        {
            Title = "About Solarpunk";
            Width = 430;
            MinWidth = 430;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ApplyChromelessDialog(this);
            Background = Cream100;
            ApplyWindowIcon(this);

            var layout = new StackPanel
            {
                Spacing = 8,
                Margin = new Thickness(20),
            };
            layout.Children.Add(DialogTitle("Sundial"));
            layout.Children.Add(new TextBlock
            {
                Text = $"Version {SolarpunkLifecycle.CurrentVersion}",
                Foreground = Ink500,
                FontSize = 13,
            });

            var errorLine = new TextBlock
            {
                Foreground = Red500,
                FontSize = 12,
                MinHeight = 18,
                TextWrapping = TextWrapping.Wrap,
            };

            var repoLink = BuildLinkButton("github.com/HumanGenome/Sundial");
            repoLink.Click += (_, _) => OpenUrlWithDialogStatus("https://github.com/HumanGenome/Sundial", errorLine);
            layout.Children.Add(repoLink);

            layout.Children.Add(errorLine);

            var close = BuildToolButton("Close");
            close.HorizontalAlignment = HorizontalAlignment.Right;
            close.Margin = new Thickness(0, 14, 0, 0);
            close.Click += (_, _) => Close();
            layout.Children.Add(close);

            Content = Panel(layout);
        }
    }

    private static TextBlock DialogTitle(string text)
    {
        return new TextBlock
        {
            Text = text,
            Foreground = Ink900,
            FontSize = 22,
            FontWeight = FontWeight.SemiBold,
        };
    }

    private static Control DialogField(string label, Control field)
    {
        var stack = new StackPanel { Spacing = 5 };
        stack.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = Ink500,
            FontSize = 12,
            FontWeight = FontWeight.Medium,
        });
        stack.Children.Add(field);
        return stack;
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? OverrideValue(string? value, string? autoValue)
    {
        // Trim what the customer typed. Empty/whitespace clears the override;
        // anything else is stored verbatim. autoValue is accepted for the
        // log-directory default-vs-explicit distinction but not otherwise used.
        _ = autoValue;
        return EmptyToNull(value);
    }

    private sealed class SignalBrushConverter : IValueConverter
    {
        public static readonly SignalBrushConverter Instance = new();

        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            SignalBrush(value?.ToString() ?? "");

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
