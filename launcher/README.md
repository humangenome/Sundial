# Sundial Launcher

Desktop launcher (Avalonia / .NET 8, Windows) that joins a player's retail
Steam Solarpunk (app **1805110**, UE 5.7) to a headless Sundial server over
direct IP. Shares its architecture with the Beacon (Subnautica 2) and Lantern
(Grounded 2) launchers, adapted to the proven Solarpunk transport (a real
retail client has joined a headless host with exactly this recipe).

## What Connect does

1. **Locate** the retail install — Steam `libraryfolders.vdf` (+ registry roots),
   Epic manifests, MS Store AppxPackage; manual override in Settings. Exe:
   `SolarpunkSteam-Win64-Shipping.exe` under
   `...\steamapps\common\Solarpunk\Solarpunk\Binaries\Win64\`.
2. **Stage UE4SS** (bundled `dist/ue4ss-client` pack: UE4SS.dll + dwmapi.dll
   proxy + UE4SS-settings.ini with EngineVersionOverride 5.7 + the 5
   PDB-derived UE4SS_Signatures + the SolarpunkConnect mod) into the game's
   Win64 dir, idempotently (version marker).
3. **Write the connect target** `%APPDATA%\Solarpunk\connect-target.txt` =
   `<host>:<port>?Name=<character>` and the identity file
   `client-identity.txt` = `STEAM <steamid64> <charHash8>` where
   steamid64 = 76561190000000000 + CRC32(lowercase name) — the same formula the
   server-side InitNewPlayer hook (SolarpunkPlugin.dll) uses to key save shards.
4. **Write the client Engine.ini** to
   `%LOCALAPPDATA%\Solarpunk\Saved\Config\Windows\Engine.ini` — the transport
   keystone. `[OnlineSubsystemSteam] bEnabled=false` is the load-bearing line
   (deregisters SteamSockets so IpNetDriver binds real UDP); OSS=Null +
   IpNetDriver NetDriverDefinitions + 300s timeouts + rate caps. Kept in
   lockstep with `Engine.ini.client` (the proven reference copy beside this
   README). A write failure FAILS the connect (the join cannot work without it).
5. **Launch the game** (drops `steam_appid.txt` = 1805110 beside the exe) and
   **inject UE4SS** post-boot via CreateRemoteThread(LoadLibraryW), retry×3
   until UE4SS.log advances — belt-and-suspenders over the dwmapi proxy (which
   does work on the current build but proxy imports die silently on game
   updates). The SolarpunkConnect mod then issues `open <target>` once a
   PlayerController exists.

## Layout

```
launcher/
├── Engine.ini.client            proven client transport config (reference copy)
├── Solarpunk.Launcher.sln
├── Directory.Build.props        version pin (0.1.0) + EnableWindowsTargeting
├── Directory.Packages.props     central package versions (Avalonia 11.2.0, BouncyCastle)
├── src/Solarpunk.Launcher/      the app (see below)
├── tests/Solarpunk.Launcher.Tests/  xunit suite (30 tests — connect-target parse,
│                                identity CRC32 vectors, UE4SS ini rewrite, mod install)
├── dist/ue4ss-client/           bundled client pack (UE4SS.dll/dwmapi.dll fetched at
│                                release time — gitignored; mods + settings + signatures committed)
├── dist/branding/               installer art (placeholder)
├── installer/solarpunk.nsi      NSIS installer (makensis -DSOLARPUNK_VERSION=x.y.z installer/solarpunk.nsi)
└── tools/load_client_ue4ss.py   Frida spawn-suspended loader (dev/diagnostic fallback;
                                 NOT needed in production — proxy + CreateRemoteThread cover it)
```

App internals (all ported from Lantern, renames + Solarpunk specifics):
MainWindow (servers deck + hero + console/RCON tab + mods tab + dialogs),
ServerStore/ServerEntry (`%APPDATA%\Solarpunk\servers.json`), CharacterStore
(`characters.json`, per-server roster + picker dialog — Solarpunk-specific, the
?Name= identity), AutoUpdater (ed25519-signed manifest at
`https://survivalservers.b-cdn.net/sundial/launcher/manifest.json` — pubkey
is a PLACEHOLDER so auto-update is cleanly disabled until the keypair lands),
RconClient (Source RCON, port+3), A2sClient (A2S_INFO, port+2),
SolarpunkHttpClient (HMAC-SHA256 API client, port+4), Ue4ssBootstrapService /
ModInstallService / Ue4ssInjector / Ue4ssLogTail / GameLaunchService /
ConnectFlow (Services/), StartupRegistration (HKCU Run + solarpunk:// scheme),
SolarpunkLifecycle (install dir/version/uninstall), tray icon + single-instance
activation pipe.

## Build

```bash
cd solarpunk/launcher
dotnet build Solarpunk.Launcher.sln          # clean on Linux/WSL (EnableWindowsTargeting) and Windows
dotnet test Solarpunk.Launcher.sln           # 44/44
dotnet publish src/Solarpunk.Launcher -c Release -r win-x64 --self-contained   # release bits for the NSI
```

Before building the installer, drop the UE4SS experimental build (5.7-capable
— the same one the headless host runs) into `dist/ue4ss-client/` as
`UE4SS.dll` + `dwmapi.dll`.

## Server-side contracts this launcher assumes

- Gameplay UDP on the base port; A2S on +2, RCON on +3, HTTP API on +4
  (standard Sundial port topology). The console/mods/players/health features are
  wired per the Lantern protocol (`/api/v1/health` with `solarpunk_version` +
  `game_build`, `/api/v1/players`, `/api/v1/manifest`, snapshots) and degrade
  gracefully (offline/empty) until the Solarpunk supervisor ships.
- `?Name=` on the travel URL → server InitNewPlayer hook mints
  76561190000000000 + CRC32(lowercase name).

## Untested (needs a real Windows machine + retail game)

- End-to-end Connect through THIS app (the underlying recipe — Engine.ini,
  UE4SS staging, post-boot injection, `open host:port?Name=` — is the exact
  sequence proven by hand against a live headless host; the launcher automates it 1:1).
- The dwmapi proxy + CreateRemoteThread double-injection interaction on the
  retail box (each path proven individually; the no-op-on-second-load is
  standard LoadLibrary refcount behavior).
- NSIS installer build + install/uninstall flow (straight clone of Lantern's).
- Auto-update end-to-end (intentionally disabled until the Solarpunk ed25519
  update keypair + signed manifest are published; flip
  `AutoUpdater.UpdateSigningPublicKeyHex`).
- solarpunk:// URL-scheme hand-off from a hosting panel (registered on startup;
  parsing is covered by the ported code paths).
