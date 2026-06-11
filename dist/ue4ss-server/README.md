# Solarpunk headless-server UE4SS distribution

Server-side UE4SS bundle: the supervisor (SolarpunkServer) drops this next to the
game exe (Solarpunk\Binaries\Win64) so the headless host loads the Solarpunk mod
stack. Mirror of the launcher's client bundle (launcher/dist/ue4ss-client) but
with the SERVER mods.

## Layout
- UE4SS-settings.ini      EngineVersionOverride 5.7 + bUseUObjectArrayCache=false
                          + the PDB-derived signature scan (UE4SS's built-in
                          scanner is wrong on 5.7 — these overrides are required).
- UE4SS_Signatures/       5 PDB-derived AOB patterns (FName_Constructor, GNatives,
                          GUObjectArray, GUObjectHashTables, StaticConstructObject).
- Mods/mods.txt           enables ONLY SolarpunkServerRuntime (orchestrator).
- Mods/SolarpunkServerRuntime  loads Host/Auth/Roster/Chat in order, writes
                          SolarpunkServer\.solarpunk-runtime-status (ready=1).
- Mods/SolarpunkHost      transport swap -> WorldSaveName -> HostGame -> net-id keying.
- Mods/SolarpunkAuth      join password gate (name-channel __SPPW__ token).
- Mods/SolarpunkRoster    roster.json (players + live X/Y/Z) for A2S/HTTP/map.
- Mods/SolarpunkChat      MOTD + inbound chat fanout.

## Runtime binaries (fetched at release, NOT committed)
UE4SS.dll + dwmapi.dll are gitignored — scripts/package-server.sh stages the
pinned UE4SS runtime into this tree before zipping for the CDN (same as the
launcher client pack). The .lua mods + settings + signatures ARE tracked.
