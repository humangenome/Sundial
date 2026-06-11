-- SolarpunkConnect v1.0.0: client join mod.
--
-- PROVEN TRANSPORT RECIPE (2026-06-09, real retail client -> headless host):
-- the launcher writes the client Engine.ini (OSS=Null + [OnlineSubsystemSteam]
-- bEnabled=false + IpNetDriver) BEFORE launch, so by the time this mod runs the
-- net layer is already on real UDP. This mod's only job is to read the connect
-- target the launcher wrote and issue the stock console `open` once a
-- PlayerController exists. The ?Name=<character> option on the line is the
-- server-side save-identity contract (InitNewPlayer hook mints a synthetic
-- SteamID64 from it).
--
-- CONTRACT — must match Solarpunk.Launcher Services/ConnectTargetService.cs:
--   TARGET_FILE = %APPDATA%\Solarpunk\connect-target.txt
--   format      = single line "<host>:<port>?Name=<charname>" (Name optional)
--
-- Derived from the proven solarpunk/server-mods/client_connect_open.lua, with
-- the hardcoded test target replaced by the launcher contract (no default —
-- if there is no target file we do nothing, so a bare game launch stays vanilla).
local APPDATA = os.getenv("APPDATA") or "C:\\Users\\Default\\AppData\\Roaming"
local DIR = APPDATA .. "\\Solarpunk"
local TARGET_FILE = DIR .. "\\connect-target.txt"
local LOG_FILE = DIR .. "\\SolarpunkConnect.lua.log"

local function log(m)
    local ok, f = pcall(io.open, LOG_FILE, "a")
    if ok and f then
        f:write(os.date("[%Y-%m-%d %H:%M:%S] ") .. tostring(m) .. "\r\n")
        f:close()
    end
    pcall(print, "[SolarpunkConnect] " .. tostring(m) .. "\n")
end

local function readTarget()
    local ok, f = pcall(io.open, TARGET_FILE, "r")
    if not (ok and f) then return nil end
    local s = f:read("*l")
    f:close()
    if s then s = s:gsub("%s+", "") end
    if s and #s > 3 then return s end
    return nil
end

local fired = false

local function go()
    local target = readTarget()
    if not target then
        log("no connect target at " .. TARGET_FILE .. " — staying vanilla")
        return
    end
    -- Defensive runtime swap. The launcher-written Engine.ini already sets
    -- IpNetDriver; this re-asserts it in case the game (or Steam cloud config)
    -- clobbered the user Engine.ini between write and boot.
    local eng = FindFirstOf("GameEngine")
    if eng and eng:IsValid() then
        pcall(function()
            local defs = eng.NetDriverDefinitions
            for i = 1, defs:GetArrayNum() do
                local d = defs[i]
                if d.DefName:ToString() == "GameNetDriver" then
                    d.DriverClassName = FName("/Script/OnlineSubsystemUtils.IpNetDriver")
                    d.DriverClassNameFallback = FName("/Script/OnlineSubsystemUtils.IpNetDriver")
                end
            end
        end)
    end
    -- Generous net timeouts so a heavy world load doesn't drop the join
    -- (mirrors the Engine.ini values onto any live driver instance).
    pcall(function()
        for _, nd in ipairs(FindAllOf("IpNetDriver") or {}) do
            nd.InitialConnectTimeout = 300.0
            nd.ConnectionTimeout = 300.0
        end
    end)
    local pc = FindFirstOf("PlayerController")
    local ksl = StaticFindObject("/Script/Engine.Default__KismetSystemLibrary")
    if ksl and ksl:IsValid() and pc and pc:IsValid() then
        log("open " .. target)
        local ok, err = pcall(function() ksl:ExecuteConsoleCommand(pc, "open " .. target, nil) end)
        if ok then log("open issued OK") else log("ERR " .. tostring(err)) end
    else
        log("not ready ksl=" .. tostring(ksl and ksl:IsValid()) .. " pc=" .. tostring(pc and pc:IsValid()))
    end
end

log("v1.0.0 loaded — waiting for PlayerController + connect target")
LoopAsync(10000, function()
    if fired then return true end
    local pc = FindFirstOf("PlayerController")
    if pc and pc:IsValid() then
        fired = true
        ExecuteInGameThread(function() go() end)
        return true
    end
    return false
end)
