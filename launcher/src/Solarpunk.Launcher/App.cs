using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace Solarpunk.Launcher;

public sealed class App : Application
{
    private TrayIcon? _trayIcon;
    private MainWindow? _mainWindow;

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Light;
        Styles.Add(new FluentTheme
        {
            Palettes =
            {
                [ThemeVariant.Light] = new ColorPaletteResources
                {
                    Accent = Color.Parse("#E8A21B"),
                },
            },
        });
        Styles.Add(MainWindow.CreateLauncherStyles());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Close-to-tray UX: clicking the title-bar X (or pressing
            // Alt+F4) hides the window instead of exiting the process.
            // MainWindow's Closing handler does the e.Cancel=true + Hide()
            // dance. We need OnExplicitShutdown so closing the (still-
            // existing) MainWindow doesn't tear down the whole app — the
            // tray icon's "Exit Solarpunk" menu item is the only path that
            // actually shuts the process down.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _mainWindow = new MainWindow();
            desktop.MainWindow = _mainWindow;
            _mainWindow.Show();

            InstallTrayIcon(desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void InstallTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        WindowIcon? icon = null;
        try
        {
            // tray-icon.ico is the color sun-over-floating-island mark at
            // tray sizes (16/32/48). icon.ico carries the same mark with a
            // 256px slot for the main window, taskbar, and installer; the
            // golden sun + sage island read well on both light and dark
            // taskbar themes.
            using var stream = AssetLoader.Open(new Uri("avares://Solarpunk.Launcher/Assets/tray-icon.ico"));
            icon = new WindowIcon(stream);
        }
        catch
        {
            // Asset missing or platform doesn't surface AssetLoader the
            // same way — fall back to no icon (Windows shows a generic
            // tray glyph). The menu still works.
        }

        var showItem = new NativeMenuItem("Show Solarpunk");
        showItem.Click += (_, _) =>
        {
            Dispatcher.UIThread.Post(() => _mainWindow?.ShowFromTray());
        };

        var exitItem = new NativeMenuItem("Exit Solarpunk");
        exitItem.Click += (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                try { _mainWindow?.RequestRealExit(); } catch { }
                try
                {
                    if (_trayIcon is not null)
                    {
                        _trayIcon.IsVisible = false;
                        _trayIcon.Dispose();
                        _trayIcon = null;
                    }
                }
                catch { }
                try { desktop.Shutdown(0); } catch { }
            });
        };

        _trayIcon = new TrayIcon
        {
            Icon = icon,
            ToolTipText = "Sundial",
            IsVisible = true,
            Menu = new NativeMenu
            {
                Items =
                {
                    showItem,
                    new NativeMenuItemSeparator(),
                    exitItem,
                },
            },
        };

        // Single left-click on the tray icon brings the window back —
        // saves customers a right-click → Show Solarpunk roundtrip on the
        // common case.
        _trayIcon.Clicked += (_, _) =>
        {
            Dispatcher.UIThread.Post(() => _mainWindow?.ShowFromTray());
        };

        // Register so Avalonia owns lifetime alongside the application.
        TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });
    }
}
