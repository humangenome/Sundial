using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Solarpunk.Launcher;

public sealed class ServerEntry : INotifyPropertyChanged
{
    private string _label = "";
    private string _serverName = "";
    private string _map = "Map unknown";
    private string _queryStatus = "Queued";
    private int _players;
    private int _maxPlayers;
    private int? _latencyMs;
    private bool _isReachable;
    private bool _isQuerying;
    private DateTimeOffset? _lastRefreshed;
    private string? _serverVersion;
    private string? _gameBuild;
    private bool _isOutdated;
    private bool _isLauncherOutdated;
    // Tri-state reflecting the A2S_INFO "visibility" byte:
    //   null  = unknown (never queried, or last query failed and we never
    //           had a successful answer)
    //   true  = server reported password-protected (the supervisor sets this
    //           when its join password is non-empty)
    //   false = server reported open
    // The pre-flight join-password prompt only fires when the saved
    // password is empty AND this is explicitly true. When it's null,
    // ConnectAsync forces a synchronous A2S query before deciding —
    // otherwise a fresh-add or cold-start entry would slip past the
    // gate and launch into a silent server-side password kick.
    private bool? _isPasswordProtected;
    private string _host;
    private int _port;
    private int _queryPort;

    public ServerEntry(string label, string host, int port, int? queryPort = null,
        string? password = null, string? lastPlayerName = null, string? adminPassword = null)
    {
        _host = host;
        _port = port;
        // Beacon convention: A2S query port is gameplay port + 2, RCON is +3,
        // HTTP API is +4. The game owns the gameplay port and doesn't
        // respond to A2S; the SolarpunkServer supervisor answers A2S/RCON/HTTP on the
        // derived ports. Callers may override (e.g. for custom panels).
        _queryPort = queryPort ?? port + 2;
        Password = password;
        LastPlayerName = lastPlayerName;
        AdminPassword = adminPassword;
        Label = string.IsNullOrWhiteSpace(label) ? $"{host}:{port}" : label;
        ServerName = Label;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Label
    {
        get => _label;
        set
        {
            if (SetField(ref _label, value))
                OnPropertyChanged(nameof(DisplayName));
        }
    }

    // Host / Port / QueryPort were originally immutable. Edit Server (v0.3.56)
    // wants in-place mutation so the existing ServerEntry instance (and its
    // status/character/auto-update bindings) survives the change instead of
    // being replaced and resetting everyone's bindings. RconPort / HttpPort
    // / Endpoint are computed off these so they update automatically.
    public string Host
    {
        get => _host;
        set
        {
            if (SetField(ref _host, value))
            {
                OnPropertyChanged(nameof(Endpoint));
                OnPropertyChanged(nameof(RconPort));
                OnPropertyChanged(nameof(HttpPort));
            }
        }
    }

    public int Port
    {
        get => _port;
        set
        {
            if (SetField(ref _port, value))
            {
                OnPropertyChanged(nameof(Endpoint));
                OnPropertyChanged(nameof(RconPort));
                OnPropertyChanged(nameof(HttpPort));
            }
        }
    }

    public int QueryPort
    {
        get => _queryPort;
        set => SetField(ref _queryPort, value);
    }

    // Optional password the server expects on connect. Null = open server.
    // Stored locally; never displayed in plain text.
    public string? Password { get; set; }

    // Per-server last player name. Auto-populated on Connect so switching
    // between servers restores the right character profile each time.
    public string? LastPlayerName { get; set; }

    // Admin (RCON) password for issuing world-transfer / snapshot / RCON
    // commands. Stored locally; null disables the World menu for this
    // server.
    public string? AdminPassword { get; set; }

    public int RconPort => Port + 3;

    public int HttpPort => Port + 4;

    public string Endpoint => $"{Host}:{Port}";

    public string ServerName
    {
        get => _serverName;
        set
        {
            if (SetField(ref _serverName, value))
                OnPropertyChanged(nameof(DisplayName));
        }
    }

    public string DisplayName => string.IsNullOrWhiteSpace(ServerName) ? Label : ServerName;

    public string Map
    {
        get => _map;
        set => SetField(ref _map, value);
    }

    public int Players
    {
        get => _players;
        set
        {
            if (SetField(ref _players, value))
            {
                OnPropertyChanged(nameof(PlayerFill));
                OnPropertyChanged(nameof(PlayerSummary));
            }
        }
    }

    public int MaxPlayers
    {
        get => _maxPlayers;
        set
        {
            if (SetField(ref _maxPlayers, value))
            {
                OnPropertyChanged(nameof(PlayerFill));
                OnPropertyChanged(nameof(PlayerSummary));
            }
        }
    }

    public int? LatencyMs
    {
        get => _latencyMs;
        set
        {
            if (SetField(ref _latencyMs, value))
                OnPropertyChanged(nameof(LatencySummary));
        }
    }

    public bool IsReachable
    {
        get => _isReachable;
        set
        {
            if (SetField(ref _isReachable, value))
                OnPropertyChanged(nameof(SignalSummary));
        }
    }

    public bool IsQuerying
    {
        get => _isQuerying;
        set
        {
            if (SetField(ref _isQuerying, value))
            {
                OnPropertyChanged(nameof(SignalSummary));
                OnPropertyChanged(nameof(PlayerSummary));
                OnPropertyChanged(nameof(LatencySummary));
            }
        }
    }

    public string QueryStatus
    {
        get => _queryStatus;
        set => SetField(ref _queryStatus, value);
    }

    public DateTimeOffset? LastRefreshed
    {
        get => _lastRefreshed;
        set => SetField(ref _lastRefreshed, value);
    }

    public double PlayerFill => MaxPlayers <= 0 ? 0 : Math.Clamp((double)Players / MaxPlayers * 100, 0, 100);

    public string PlayerSummary => IsQuerying ? "" : MaxPlayers <= 0 ? "" : $"{Players} of {MaxPlayers} players";

    public string LatencySummary => IsQuerying ? "" : LatencyMs is null ? "" : $"{LatencyMs} ms";

    public string SignalSummary => IsQuerying
        ? "Checking"
        : !IsReachable ? "Offline"
        : IsOutdated ? "Online (outdated)"
        : "Online";

    /// <summary>
    /// Supervisor version the server reports via /api/v1/health. Compared
    /// against the launcher's own version to surface 'this server is
    /// running an older build' in the UI.
    /// </summary>
    public string? ServerVersion
    {
        get => _serverVersion;
        set => SetField(ref _serverVersion, value);
    }

    /// <summary>
    /// Solarpunk game build string from <c>/api/v1/health</c>. Surfaced in
    /// the hero meta line next to the supervisor version so admins can spot
    /// when the developer has shipped a new game build that the host hasn't
    /// picked up yet.
    /// </summary>
    public string? GameBuild
    {
        get => _gameBuild;
        set => SetField(ref _gameBuild, value);
    }

    public bool IsOutdated
    {
        get => _isOutdated;
        set
        {
            if (SetField(ref _isOutdated, value))
                OnPropertyChanged(nameof(SignalSummary));
        }
    }

    /// <summary>
    /// True when the server reports a supervisor version newer than the
    /// running launcher — i.e. the client needs to update, not the server.
    /// Symmetric counterpart to <see cref="IsOutdated"/>.
    /// </summary>
    public bool IsLauncherOutdated
    {
        get => _isLauncherOutdated;
        set => SetField(ref _isLauncherOutdated, value);
    }

    /// <summary>
    /// Tri-state. See _isPasswordProtected backing-field doc-comment for
    /// the null/true/false semantics. true is the only state that triggers
    /// the pre-flight prompt without further checking; null forces a
    /// fresh ping in ConnectAsync.
    /// </summary>
    public bool? IsPasswordProtected
    {
        get => _isPasswordProtected;
        set => SetField(ref _isPasswordProtected, value);
    }

    public void MarkQuerying()
    {
        IsQuerying = true;
        QueryStatus = "Refreshing";
    }

    public void ApplyInfo(A2sClient.A2sInfo? info)
    {
        IsQuerying = false;
        LastRefreshed = DateTimeOffset.Now;

        if (info is null)
        {
            IsReachable = false;
            Map = "Offline";
            Players = 0;
            MaxPlayers = 0;
            LatencyMs = null;
            QueryStatus = "No response";
            // Do NOT clear IsPasswordProtected when the server goes
            // offline. The last known value is still our best guess for
            // the pre-flight prompt — a server that was passworded
            // yesterday probably still is. If we never had a successful
            // query in the first place, this stays null (unknown) and
            // ConnectAsync's pre-flight check will force a fresh query
            // before launching.
            return;
        }

        IsReachable = true;
        IsPasswordProtected = info.IsPasswordProtected;
        ServerName = string.IsNullOrWhiteSpace(info.Name) ? Label : info.Name;
        // The user-facing status line
        // already has a dedicated reachability dot + ping + player count.
        // Show "Online" instead of the map name so the card reads as
        // status, not as game-internal trivia. Falls back to the actual
        // map name only if the A2S response is reachable but missing the
        // map field (defensive — shouldn't happen on a healthy server).
        Map = "Online";
        Players = info.Players;
        MaxPlayers = info.MaxPlayers;
        LatencyMs = info.LatencyMs;
        QueryStatus = "Online";
    }

    public void ApplyHttpOnline(string? serverName, int players, int maxPlayers)
    {
        IsQuerying = false;
        LastRefreshed = DateTimeOffset.Now;
        IsReachable = true;
        if (!string.IsNullOrWhiteSpace(serverName))
            ServerName = serverName;
        Map = "Online";
        Players = Math.Max(0, players);
        MaxPlayers = Math.Max(0, maxPlayers);
        LatencyMs = null;
        QueryStatus = "Online";
    }

    public override string ToString() => DisplayName;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        if (propertyName is not null)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
