<p align="center">
  <img src="launcher/dist/branding/solarpunk-icon-1024.png" alt="Sundial" width="180">
</p>

<p align="center">
  <a href="#install"><img src="https://img.shields.io/badge/Platform-Windows_10%2F11-blue.svg" alt="Platform"></a>
  <a href="https://store.steampowered.com/app/1805110/"><img src="https://img.shields.io/badge/Game-Solarpunk-darkgreen.svg" alt="Game"></a>
  <a href="https://github.com/HumanGenome/Sundial"><img src="https://img.shields.io/badge/License-MIT-lightgrey.svg" alt="License"></a>
</p>

# Sundial

Sundial gives **Solarpunk** IP/port multiplayer on dedicated servers: players join through the Sundial desktop app, hosts run a Sundial server package next to the Solarpunk game files, and worlds stay on the server with per-character saves, password auth, snapshots, admin tools, server query, a live web map, and a server-side chat feed. No session invites, no host-must-be-online — the world runs 24/7 whether anyone is on or not.

Every player must install Sundial to join a Sundial server. Stock Solarpunk cannot connect to Sundial servers directly.

## Features

### 🧭 Join by IP and port
Add a server address once, pick a character, and connect straight into the hosted world. Sundial derives the query, RCON, and admin ports from the gameplay port, so you only ever enter one port.

### 💾 Server-side worlds
The world lives on the host machine and keeps running after players leave. SolarpunkServer supervises a headless copy of the game, watches its health, and restarts it if it crashes.

### 🧑‍🌾 Per-character saves
Each character name maps to its own stable save identity on the server, so several people (or one person with several characters) keep separate progress on the same world. Sundial remembers the character you used for each saved server.

### 🔁 Snapshots and rollback
Admins can take world snapshots and list saved worlds from RCON (`save snapshot`, `save list`), giving a recovery point before major changes.

### 🛠 Admin console
SolarpunkServer exposes Source RCON for `status`, `players`, `ping`, `save snapshot`, `save list`, `say`, `announce`, `motd`, and a ban system (`ban`, `unban`, `bans`). The Sundial app has a built-in console tab.

### 📡 Server query
SolarpunkServer answers Source A2S query so server lists, monitoring tools, and bots can read status and player counts.

### 🔒 Password auth
Set a server password and Sundial gates joins on it server-side, kicking anyone who doesn't match. Solarpunk has no native password handling, so Sundial provides its own.

### 🗺 Live web map + roster
The server tracks connected players and their in-world positions and serves a live map at `/map/` plus a player roster over HTTP, so you can see who's online and where they are without being in the game.

### 💬 Server chat + MOTD
The server keeps a chat feed with an MOTD, admin `say`/`announce` broadcasts, and an HTTP chat API. An in-game overlay mod exists as a **preview** — it is not enabled by default yet.

### 🧩 Mods
Sundial loads Lua and C++ mods through UE4SS on both the client and the host, and servers publish a mod manifest (required / recommended / blocked) that the app shows per server.

## Install

### Managed hosting
The shortest path is [SurvivalServers.com Solarpunk hosting](https://www.survivalservers.com/services/game_servers/solarpunk/?utm_source=github&utm_medium=readme_install&utm_campaign=sundial). Sundial is already installed, ports are handled, and the panel shows a one-click setup link for players.

### Players
1. Download `SundialSetup-<version>.exe` from the [latest release](https://github.com/HumanGenome/Sundial/releases/latest).
2. Run the installer. Windows SmartScreen may warn because the installer is not code-signed yet; choose **More info** then **Run anyway**.
3. Open Sundial, add the server address, pick a character name, and click **Connect**.

Sundial checks for app updates automatically on launch and while it is running.

### Self-hosted servers
1. Download `SundialServer-<version>.zip` from the [latest release](https://github.com/HumanGenome/Sundial/releases/latest).
2. Extract it on the Windows host.
3. Edit `SolarpunkServer\appsettings.json` (ports, server name, passwords).
4. **Install the Solarpunk game files** into the folder set as `GameInstallRoot` (default `C:\Solarpunk\game`) — SolarpunkServer launches the game from there, it does not ship the game. Either copy your existing `Steam\steamapps\common\Solarpunk` folder into that path, or install it with SteamCMD using a Steam account that owns Solarpunk: `steamcmd +force_install_dir C:\Solarpunk\game +login <account> +app_update 1805110 validate +quit`. The server runs headless — no GPU is required.
5. Forward/open the gameplay UDP port, query UDP port, RCON TCP port, and admin HTTP TCP port.
6. Run `SolarpunkServer\SolarpunkServer.exe`.

### Port topology
All side-channel ports derive from the gameplay port:

| Port | Use |
|---|---|
| base | Solarpunk gameplay (UDP) |
| +2 | Source A2S query (UDP) |
| +3 | Source RCON (TCP) |
| +4 | Admin HTTP API + live map (TCP) |

## FAQ

### How do I join a server?
Open Sundial, click Add Server, and enter a name, the server's IP or hostname, and its gameplay port. Add the server password if it has one. Select the server, pick a character name, and click Connect — Sundial stages the client-side UE4SS pack, writes the transport config, launches Solarpunk, and joins automatically.

### Can I run the server on the same machine I play on?
No. The Sundial server runs its own copy of Solarpunk in the background and your game client runs a second copy, and Steam only allows one running instance of a game per account. Run the server on a separate host and connect to it from your gaming PC.

### Is Linux supported?
No. Sundial is Windows-only, for both the player app and the server (Windows 10/11, 64-bit). The server launches the Windows build of Solarpunk and uses Windows process management.

## Releases

The public release page is intentionally simple:

- `SundialSetup-<version>.exe` — installer for players
- `SundialServer-<version>.zip` — server package for hosts

If you only want to play on someone else's server, you need the installer, not the server zip. Source archives are generated by GitHub automatically for tags.

## Development

This repository contains the Sundial app, server, runtime mods, installer, and release scripts.

- `launcher/src/Solarpunk.Launcher` — Avalonia desktop app (servers, characters, console, mods, settings)
- `server/SolarpunkServer` — host supervisor, RCON, A2S query, HTTP admin API, snapshots, live map
- `native/SolarpunkPlugin` — native runtime plugin
- `dist/ue4ss-server` — server-side UE4SS layout (settings, scan signatures, the Solarpunk mod stack)
- `launcher/dist/ue4ss-client` — client-side UE4SS layout the app installs (the SolarpunkConnect join mod)
- `client-mods` — optional client overlay mods (chat HUD preview)
- `installer` — NSIS installer (`launcher/installer/solarpunk.nsi`)
- `scripts` — server packaging + signed auto-update manifest publishing

Common local checks:

```bash
cd launcher && dotnet build Solarpunk.Launcher.sln && dotnet test Solarpunk.Launcher.sln
cd server && dotnet build Solarpunk.Server.sln
bash scripts/package-server.sh v0.1.0
```

UE4SS runtime binaries (`UE4SS.dll`, `dwmapi.dll`) are not committed — `scripts/package-server.sh` stages a pinned UE4SS build into the bundles at release time (set `UE4SS_RUNTIME_DIR` or `UE4SS_ZIP_URL`).

## Known Limitations

A few rough edges relative to stock Solarpunk:

- **In-game chat overlay is a preview.** The server-side chat feed, MOTD, and HTTP chat API are live, but the client overlay mod has not had its on-screen rendering visually signed off and is not enabled by default.
- **Windows only.** Both the app and the server package are Windows 10/11 x64.
- **No code signing yet.** SmartScreen will warn on first run of the installer; build-from-source is the escape hatch if you don't want to trust an unsigned binary.

## Community Note

Sundial is a community project and is not affiliated with or endorsed by the developers of Solarpunk.

## License

See [LICENSE](LICENSE).

## Credits

- [UE4SS](https://github.com/UE4SS-RE/RE-UE4SS) — Unreal Engine scripting and modding framework
- [Avalonia](https://avaloniaui.net/) — .NET UI framework used by Sundial
