"""
Solarpunk client-side Frida loader (dev/diagnostic alternative to the launcher).

PORT FROM BEACON (beacon/src/tools/Beacon.FridaLoader/load_client_ue4ss.py),
adapted for Solarpunk (Steam app 1805110, SolarpunkSteam-Win64-Shipping.exe).

The production Solarpunk launcher does NOT need Frida: the dwmapi.dll proxy
auto-loads UE4SS on the current Solarpunk build, and the launcher belt-and-
suspenders with a post-boot CreateRemoteThread(LoadLibraryW) injection
(Services/Ue4ssInjector.cs). This tool exists for parity with Beacon's loader
and as the fallback if a future game build hardens DLL loading the way SN2's
UE5.6 did (SetDefaultDllDirectories defeating proxy DLLs) — spawn-suspended +
gum Module.load sidesteps that entirely.

Spawn mode starts retail Solarpunk suspended, injects UE4SS.dll via Frida's
gum-runtime Module.load, then resumes. Attach mode waits for a Steam-launched
Solarpunk process and uses the same Module.load path.

Also writes the PROVEN client Engine.ini (OSS=Null + [OnlineSubsystemSteam]
bEnabled=false + IpNetDriver — the load-bearing bEnabled=false deregisters
SteamSockets so IpNetDriver binds real UDP; keep in lockstep with
solarpunk/launcher/Engine.ini.client) and the connect-target file the
SolarpunkConnect Lua mod reads (%APPDATA%\\Solarpunk\\connect-target.txt,
format "host:port?Name=<character>").
"""
import argparse
import frida
import json
import os
import sys
import time
from dataclasses import dataclass
from typing import Optional


GAME_PROCESS_NAME = "SolarpunkSteam-Win64-Shipping.exe"

ENGINE_INI_CLIENT = """[OnlineSubsystem]
DefaultPlatformService=Null

[OnlineSubsystemSteam]
bEnabled=false

[OnlineSubsystemNull]
bSimulateForwarded=true

[/Script/Engine.GameEngine]
!NetDriverDefinitions=ClearArray
+NetDriverDefinitions=(DefName="GameNetDriver",DriverClassName="/Script/OnlineSubsystemUtils.IpNetDriver",DriverClassNameFallback="/Script/OnlineSubsystemUtils.IpNetDriver")

[/Script/Engine.Engine]
!NetDriverDefinitions=ClearArray
+NetDriverDefinitions=(DefName="GameNetDriver",DriverClassName="/Script/OnlineSubsystemUtils.IpNetDriver",DriverClassNameFallback="/Script/OnlineSubsystemUtils.IpNetDriver")

[/Script/OnlineSubsystemUtils.IpNetDriver]
NetConnectionClassName=/Script/OnlineSubsystemUtils.IpConnection
ConnectionTimeout=300.0
InitialConnectTimeout=300.0
MaxClientRate=2097152
MaxInternetClientRate=2097152

[/Script/Engine.Player]
ConfiguredInternetSpeed=2097152
ConfiguredLanSpeed=2097152

[/Script/Engine.GameNetworkManager]
TotalNetBandwidth=16777216
MaxDynamicBandwidth=2097152
MinDynamicBandwidth=524288
"""


def default_user_config_dir() -> str:
    # Packaged UE5 games key the user config dir off the project name
    # ("Solarpunk"): %LOCALAPPDATA%\Solarpunk\Saved\Config\Windows
    local = os.environ.get("LOCALAPPDATA") or os.path.expanduser("~\\AppData\\Local")
    return os.path.join(local, "Solarpunk")


def write_client_engine_ini(user_dir: str) -> None:
    cfg_dir = os.path.join(user_dir, "Saved", "Config", "Windows")
    os.makedirs(cfg_dir, exist_ok=True)
    ini = os.path.join(cfg_dir, "Engine.ini")
    with open(ini, "w", encoding="ascii") as f:
        f.write(ENGINE_INI_CLIENT)
    print(f"wrote {ini}")


def write_connect_target(host: str, port: int, name: Optional[str]) -> None:
    appdata = os.environ.get("APPDATA") or os.path.expanduser("~\\AppData\\Roaming")
    sp_dir = os.path.join(appdata, "Solarpunk")
    os.makedirs(sp_dir, exist_ok=True)
    target_file = os.path.join(sp_dir, "connect-target.txt")
    line = f"{host}:{port}"
    if name:
        line += f"?Name={name}"
    with open(target_file, "w", encoding="ascii") as f:
        f.write(line)
    print(f"wrote {target_file} -> {line}")


def find_plugin_dll(explicit: Optional[str], ue4ss_dll: str) -> Optional[str]:
    # The (optional) native client plugin. Not currently required for Solarpunk —
    # the server-side InitNewPlayer hook owns net-id minting — but kept for
    # parity with Beacon.dll's slot if the client-side
    # ULocalPlayer::GetPreferredUniqueNetId hook ever ships.
    if explicit:
        return explicit

    ue4ss_dir = os.path.dirname(os.path.abspath(ue4ss_dll))
    candidates = [
        r"C:\Solarpunk\SolarpunkPlugin.dll",
        os.path.join(ue4ss_dir, "SolarpunkPlugin.dll"),
        os.path.join(ue4ss_dir, "Mods", "SolarpunkPlugin", "dlls", "main.dll"),
        os.path.join(os.path.dirname(ue4ss_dir), "SolarpunkPlugin.dll"),
    ]
    for candidate in candidates:
        if os.path.isfile(candidate):
            return candidate
    return None


@dataclass(frozen=True)
class TargetProcess:
    pid: int
    name: str


def module_load_script(modules: list[tuple[str, str]]) -> str:
    return """
        const modules = %s;
        const results = [];
        for (const item of modules) {
            const label = item[0];
            const path = item[1];
            try {
                const mod = Module.load(path);
                results.push({ label, ok: true, base: mod.base.toString(), name: mod.name });
            } catch (e) {
                results.push({ label, ok: false, error: e.toString() });
            }
        }
        send({ results });
    """ % json.dumps([(label, path) for label, path in modules])


def inject_modules(
    session,
    pid: int,
    modules: list[tuple[str, str]],
    wait_s: int,
    after_load=None,
) -> int:
    js = module_load_script(modules)

    result = {}
    def on_msg(m, _):
        if m.get("type") == "send":
            result["payload"] = m.get("payload")
        elif m.get("type") == "error":
            result["err"] = m

    s = session.create_script(js)
    s.on("message", on_msg)
    s.load()
    if after_load:
        after_load()

    deadline = time.time() + wait_s
    while time.time() < deadline and "payload" not in result and "err" not in result:
        time.sleep(0.1)

    if "payload" in result:
        p = result["payload"]
        failed = False
        for item in p.get("results", []):
            if item.get("ok"):
                print(f"{item['label']} loaded into pid={pid} base={item['base']}")
            else:
                print(f"{item['label']} load FAILED: {item.get('error')}")
                failed = True
        if failed:
            return 4
    elif "err" in result:
        print(f"script error: {result['err']}"); return 5
    else:
        print("(no response - UE4SS may still be initializing)")

    return 0


def build_module_list(ue4ss_dll: str, plugin_dll: Optional[str]) -> list[tuple[str, str]]:
    if not os.path.isfile(ue4ss_dll):
        print(f"ue4ss dll not found: {ue4ss_dll}", file=sys.stderr); raise SystemExit(3)
    if plugin_dll and not os.path.isfile(plugin_dll):
        print(f"SolarpunkPlugin.dll not found: {plugin_dll}", file=sys.stderr); raise SystemExit(6)

    modules = [("UE4SS", ue4ss_dll)]
    if plugin_dll:
        modules.append(("SolarpunkPlugin", plugin_dll))
    else:
        print("SolarpunkPlugin.dll not found; native client net-id hook unavailable (not currently required)")
    return modules


def process_name_matches(actual: str, wanted: str) -> bool:
    a = actual.lower()
    w = wanted.lower()
    if a == w:
        return True
    if w.endswith(".exe"):
        return a == w[:-4]
    return a == f"{w}.exe"


def try_find_process_by_name(process_name: str) -> Optional[TargetProcess]:
    device = frida.get_local_device()
    for p in device.enumerate_processes():
        if process_name_matches(p.name, process_name):
            return TargetProcess(pid=p.pid, name=p.name)
    return None


def attach_ue4ss(
    attach_by_name: str,
    ue4ss_dll: str,
    plugin_dll: Optional[str],
    attach_timeout_s: int = 90,
    wait_s: int = 5,
) -> int:
    modules = build_module_list(ue4ss_dll, plugin_dll)
    deadline = time.time() + attach_timeout_s
    last_error = None

    while time.time() < deadline:
        try:
            target = try_find_process_by_name(attach_by_name)
            if target is None:
                time.sleep(0.5)
                continue

            print(f"attach: {target.name} pid={target.pid}")
            session = frida.attach(target.pid)
            rc = inject_modules(session, target.pid, modules, wait_s)

            try: session.detach()
            except Exception: pass
            print(f"pid={target.pid} running detached; SolarpunkConnect fires once World+PC are valid")
            return rc
        except Exception as e:
            last_error = e
            print(f"attach attempt failed: {e}", file=sys.stderr)
            time.sleep(0.5)

    if last_error:
        print(f"timed out waiting to attach to {attach_by_name}: {last_error}", file=sys.stderr)
    else:
        print(f"timed out waiting for {attach_by_name}", file=sys.stderr)
    return 7


def load_ue4ss(
    game_exe: str,
    ue4ss_dll: str,
    plugin_dll: Optional[str],
    args_extra: list[str],
    wait_s: int = 5,
) -> int:
    if not os.path.isfile(game_exe):
        print(f"game exe not found: {game_exe}", file=sys.stderr); return 2

    modules = build_module_list(ue4ss_dll, plugin_dll)

    full_args = [game_exe, "-log"] + list(args_extra)
    print(f"spawn: {' '.join(full_args)}")
    pid = frida.spawn(full_args[0], argv=full_args)
    session = frida.attach(pid)

    def resume_spawned_process():
        time.sleep(0.5)
        frida.resume(pid)

    rc = inject_modules(session, pid, modules, wait_s, resume_spawned_process)

    try: session.detach()
    except Exception: pass
    print(f"pid={pid} running detached; SolarpunkConnect fires once World+PC are valid")
    return rc


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--exe", default=r"C:\Program Files (x86)\Steam\steamapps\common\Solarpunk\Solarpunk\Binaries\Win64\SolarpunkSteam-Win64-Shipping.exe",
                    help="path to retail SolarpunkSteam-Win64-Shipping.exe")
    ap.add_argument("--ue4ss", required=True, help="path to UE4SS.dll")
    ap.add_argument("--plugin", default="", help="path to SolarpunkPlugin.dll (optional; client net-id hook)")
    ap.add_argument("--host", required=True, help="server host (IP or DNS)")
    ap.add_argument("--port", type=int, required=True, help="server gameplay port")
    ap.add_argument("--name", default="", help="character name (?Name= save-identity option)")
    ap.add_argument("--user-dir", default="", help="override the user config root (default %LOCALAPPDATA%\\Solarpunk)")
    ap.add_argument("--extra", default="", help="extra game args (space-separated)")
    ap.add_argument("--attach-by-name", default="", help="attach to an already-running process by image name instead of spawning")
    ap.add_argument("--attach-timeout", type=int, default=90, help="seconds to wait for --attach-by-name process")
    args = ap.parse_args()

    write_client_engine_ini(args.user_dir or default_user_config_dir())
    write_connect_target(args.host, args.port, args.name or None)
    plugin_dll = find_plugin_dll(args.plugin or None, args.ue4ss)
    extra_list = args.extra.split() if args.extra else []
    if args.attach_by_name:
        sys.exit(attach_ue4ss(args.attach_by_name, args.ue4ss, plugin_dll, args.attach_timeout))
    sys.exit(load_ue4ss(args.exe, args.ue4ss, plugin_dll, extra_list))
