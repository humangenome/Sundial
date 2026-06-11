# dist/ue4ss-client — client-side UE4SS layout

This is the UE4SS layout the Sundial launcher installs alongside the player's
retail Solarpunk so a player can join a headless Sundial server. It is the
CLIENT pack (shipped to players inside the launcher installer), NOT the host
overlay — the server-side mods live in `solarpunk/server-mods/`.

## Layout

```
ue4ss-client/
├── UE4SS.dll                 (fetched at release time, gitignored — UE4SS
│                              experimental build with 5.7 support; the SAME
│                              build the headless host runs)
├── dwmapi.dll                (UE4SS proxy, fetched at release time — proven to
│                              auto-load on the current Solarpunk build; the
│                              launcher also post-boot injects UE4SS.dll via
│                              CreateRemoteThread as a belt-and-suspenders)
├── UE4SS-settings.ini        (EngineVersionOverride 5.7 + AOB scan tuning)
├── UE4SS_Signatures/         (5 PDB-derived scan patterns for the Solarpunk
│                              build — FName ctor, GNatives,
│                              StaticConstructObject, GUObjectHashTables,
│                              GUObjectArray — derived from the game's shipped
│                              PDB; UE4SS's built-in scan is wrong on 5.7)
└── Mods/
    ├── mods.txt              client mod load list
    └── SolarpunkConnect/     the join mod
        ├── enabled.txt
        └── Scripts/main.lua
```

## SolarpunkConnect

Reads the `<host>:<port>?Name=<character>` line the launcher writes to
`%APPDATA%\Solarpunk\connect-target.txt`, then issues the stock Unreal console
`open <line>` via KismetSystemLibrary once a PlayerController exists. The
transport itself (OSS=Null + `[OnlineSubsystemSteam] bEnabled=false` +
IpNetDriver) is configured by the Engine.ini the launcher writes to
`%LOCALAPPDATA%\Solarpunk\Saved\Config\Windows\Engine.ini` BEFORE launch — the
load-bearing line is `bEnabled=false`, which deregisters SteamSockets so
IpNetDriver binds real UDP (see solarpunk/launcher/Engine.ini.client).

The `?Name=` option is the save-identity contract: the headless host derives a
synthetic SteamID64 (76561190000000000 + CRC32(lowercase name)) from it so each
character name maps to a stable save shard.
