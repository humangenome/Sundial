-- SolarpunkConnect v1.1.0: client join mod + name-channel keeper.
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

-- ---------------------------------------------------------------------------
-- Name-channel keeper (v1.1.0, game build 23665180+).
--
-- Build churn broke the ?Name= carrier: since 23665180 the game re-applies its
-- platform profile name (OSS Null = the machine name, e.g. RYZEN3-PC-...) to
-- the PlayerState after join, overwriting the URL's ?Name=<char>[__SPPW__<pw>].
-- The whole server identity stack (synthetic save/load key, auth token, roster)
-- reads PlayerState:GetPlayerName(), so the character identity — and password
-- auth on locked servers — died with it. Verified on the old build (23659698)
-- the URL name stuck (roster id was crc32 of the character name).
--
-- Fix: re-assert the name from the client with the stock engine RPC
-- APlayerController:ServerChangeName(<full ?Name= value>) whenever the local
-- PlayerState disagrees. Reliable RPC, last-write-wins, and the server-side
-- SolarpunkAuth sweep explicitly tolerates a name that lands via "a post-join
-- ServerChangeName" (its grace window exists for exactly this). The loop runs
-- for the whole session (cheap: one compare every 2s, RPC only on mismatch) so
-- a later profile re-apply can never silently flip the save key mid-session.
-- ---------------------------------------------------------------------------
local function targetName()
    local t = readTarget()
    if not t then return nil end
    local i = t:find("?Name=", 1, true)
    if not i then return nil end
    local nm = t:sub(i + 6)
    if #nm == 0 then return nil end
    return nm
end

local lastAsserted = 0
local function keepName()
    local want = targetName()
    if not want then return end
    pcall(function()
        local pc = FindFirstOf("PlayerController")
        if not (pc and pc:IsValid()) then return end
        local ps = pc.PlayerState
        if not (ps and ps:IsValid()) then return end
        local have = ps:GetPlayerName():ToString()
        if have ~= want then
            pc:ServerChangeName(want)
            local now = os.time()
            if now - lastAsserted >= 30 then
                log("name re-assert: '" .. tostring(have) .. "' -> '" .. want .. "'")
                lastAsserted = now
            end
        end
    end)
end

log("v1.1.0 loaded — waiting for PlayerController + connect target")
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

-- Name keeper: low-frequency, game-thread, whole-session.
LoopAsync(2000, function()
    if not fired then return false end
    ExecuteInGameThread(function() keepName() end)
    return false
end)
