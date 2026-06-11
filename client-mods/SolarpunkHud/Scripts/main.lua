-- SolarpunkHud v0.1.0 — in-game chat overlay + input for Solarpunk co-op
-- servers. Direct port of BeaconHud (SN2); the crash-safe UMG architecture,
-- keybind capture, feed polling, and outbox are carried over verbatim. The
-- ONLY game-specific change is the reparent target (see reparent_watermark):
-- Solarpunk has no watermark widget, so the overlay hosts on a discovered
-- native HUD widget (default SW_IngameMessage_C) and renders into the first
-- child UTextBlock of its WidgetTree.
--
-- Solarpunk has no native text chat, so this mod is BOTH the player's read
-- window into admin broadcasts/other players and the only place a player
-- can type chat on the server.
--
-- *** VISUAL CONFIRMATION STILL PENDING ***
-- A headless (-nullrhi) client cannot present a UMG overlay, so the render was
-- NOT visually verified on the box. The mod is code-complete and boots
-- crash-free (the reparent is try-once + fail-closed: if no host widget/text
-- child is found it no-ops, never an AV). The exact host widget, its child
-- text block, and the on-screen position must be confirmed by eye on a
-- WINDOWED client; the host class can be overridden via
-- %APPDATA%\Solarpunk\client-hud-host.txt. Same limitation SN2/BeaconHud hit
-- (needed a human eyeball for the visual sign-off).
--
-- All "Beacon"/"SN2" references below are the original port provenance.
--
-- ====================================================================
-- v0.8.11 KEYBIND MODIFIER GAPS (same class as v0.8.10's Enter fix):
--   - Shift+letter now returns true so a shifted character is consumed while
--     typing instead of also firing the game key.
--   - Backspace and the arrow keys gained Shift variants — a plain bind is
--     dead while Shift is held, so e.g. backspacing right after a capital
--     used to do nothing.
--
-- ====================================================================
-- v0.8.10 STUCK-OPEN CHAT BOX (movement frozen until Esc) — two edge fixes:
--   (1) Shift/Ctrl+Enter now send while typing. A plain Enter bind doesn't
--       fire while a modifier is held, so finishing a message with "!" or a
--       capital and hitting Enter a beat early sent NOTHING and left the box
--       open with movement frozen. (Shift+Esc cancels too.)
--   (2) The 15s frozen-movement failsafe is now keyed to a typing-only
--       keystroke clock, not render.LastActivity — incoming chat from other
--       players bumped that clock, so on a busy server the failsafe never
--       fired and a stuck-open box stayed frozen forever.
-- Pure keybind + timestamp changes; nothing touches input mode or focus.
--
-- ====================================================================
-- v0.8.3 DEAD WASD / MOUSE-LOOK AFTER TYPING — ROOT-CAUSE FIX
-- ====================================================================
-- Through v0.8.2, after sending ONE chat line, WASD and mouse-look went
-- completely dead even though the PlayerController + pawn were provably in a
-- normal state (ignoreMove=false ignoreLook=false cursor=false, speeds/jump
-- restored). The blocker was the VIEWPORT/Slate input ROUTING, not movement.
--
--   CAUSE: release_ui_capture() called UWidgetBlueprintLibrary
--     SetInputMode_GameOnly(pc) + SetFocusToGameViewport() + ClearKeyboardFocus
--     / ClearUserFocus on every typing-end. On this exact UE4SS/UE5.6/D3D12
--     build those "reclaim" calls DETACH keyboard/mouse-axis routing from the
--     live FSceneViewport, so raw input never reaches PlayerInput again. The
--     proven Nexus "MultiplayerChat" mod NEVER touches input mode or viewport
--     focus — only freezes/unfreezes movement — and movement works fine in it.
--
--   FIX: release_ui_capture() is now MOVEMENT-ONLY. It does NOT call
--     SetInputMode_GameOnly, SetFocusToGameViewport, ClearKeyboardFocus, or
--     ClearUserFocus. open_typing() already grabs NO keyboard focus (global
--     RegisterKeyBind hooks capture keys without it) and the overlay is
--     HitTestInvisible, so nothing ever captured input that needs reclaiming.
--     The function now only re-hides the cursor (plain bool) and logs a full
--     input/movement diagnostic so the next test can confirm viewport/PC state
--     before-vs-after typing.
--
--   CONFIGURABLE OPEN KEY: ENTER + NUM_ENTER always open chat (proven, never a
--     bound SN2 letter action) and are CONSUMED only when chat actually opens.
--     Drop client-chat-open-key.txt with "Y" / any A-Z (or "ENTER") to also
--     open chat on that letter; a configured letter only consumes the key when
--     chat opens, so it can never also fire the game action.
--
-- ====================================================================
-- v0.8.2 MULTI-LINE CHAT HISTORY ON THE NATIVE WATERMARK (brick-proof)
-- ====================================================================
-- v0.8.1 only ever showed ONE line (the single newest message) because the
-- reparented native WBP_Watermark.VersionInfo UTextBlock is single-line and
-- ignores embedded "\n". v0.8.2 flips that SAME live native widget to multi-
-- line so it can render the last ~8 messages + the typing line stacked.
--
--   HOW: try-once, on the game thread, all pcall'd — set the PLAIN BOOL
--     property VersionInfo.AutoWrapText = true (the exact write shape already
--     shipped at the feed-row construction below + style_versioninfo), set
--     the float VersionInfo.MinDesiredWidth = WIDGET_W to give a finite wrap
--     width, then the no-arg SynchronizeProperties() flush. AutoWrapText +
--     finite width makes a native UTextBlock honor both soft-wrap AND embedded
--     "\n", so a "\n"-joined transcript stacks instead of clipping to one line.
--     NO new widget is created (zero StaticConstructObject/AddToViewport), so
--     the uncatchable native-paint AV class is never touched.
--
--   GUARANTEED FALLBACK: if the AutoWrapText write pcall fails OR the widget
--     never goes multi-line-ready, render.MultiLineDisabled latches true
--     FOREVER (no retry/spam) and the selector falls straight back to the
--     proven v0.8.1 single-line build_watermark_line() render. The flag is
--     re-armed only when reparent_watermark() rebinds a fresh VersionInfo
--     (map travel), exactly like StyleApplied.
--
-- ====================================================================
-- v0.8.1 BRICK-PROOFING: WATERMARK SHIPS BY DEFAULT, RICH PANEL IS OPT-IN
-- ====================================================================
-- v0.8.1 hard-locks the ship default to the PROVEN single-line watermark.
-- The hand-built UserWidget -> AddToViewport rich panel is an UNCATCHABLE
-- native-paint AV risk on THIS exact UE4SS/UE5.6/D3D12 build: if Slate's
-- first RebuildWidget/paint of a bare runtime-constructed UserWidget access-
-- violates, the surrounding pcall CANNOT catch it (pcall does not contain a
-- paint-thread crash) and the game dies. The user has been bricked by this
-- class of bug repeatedly, so the rich panel is DISABLED BY DEFAULT and only
-- enabled behind a launcher marker file (PANEL_FLAG_FILE, mirroring the DEBUG
-- flag) once it is externally validated in-game on this build.
--
--   MSG-DISPLAY FIX (the standalone, low-risk win — always on): the native
--     WBP_Watermark VersionInfo block is a SINGLE-LINE build/version text
--     block. v0.7.2 wrote a newline-joined multi-line transcript into it;
--     that block does NOT render embedded "\n", so the feed collapsed to one
--     clipped line and read as blank. The watermark fallback now renders ONE
--     line only (the typing line while typing, else the single newest message,
--     else a fresh MOTD, else "Press Enter to chat"). This is the guaranteed
--     surface and it ships ON.
--
--   RICH PANEL (opt-in, default OFF): a UserWidget -> CanvasPanel -> Border
--     (navy glass) -> VerticalBox -> [cyan header, ScrollBox{ feed VerticalBox
--     of TextBlock rows }, dim input]. Built with StaticConstructObject_
--     Internal(class, WidgetTree) and AddToViewport. Gated behind
--     PANEL_FLAG_FILE: absent (the default) => the panel path is NEVER even
--     attempted, so there is zero exposure to the native-paint AV. Present
--     (a dev/validation opt-in) => it is tried ONCE, hardened with a verified-
--     attach check (PanelAdded), forever-disable-on-first-failure, and a
--     RemoveFromParent cleanup so a half-built root can never leak or suppress
--     the watermark.
--
--   TRY-ONCE + GUARANTEED FALLBACK: even when opted in, the panel is tried
--     ONCE. If the build fails (catchably) OR a built panel later invalidates
--     OR never verifiably attaches to the viewport, the panel is disabled
--     FOREVER and the proven one-line watermark takes over — no per-tick retry
--     spam, no game-thread starvation, and a dead/unattached panel can NEVER
--     collapse the watermark (panel_valid() requires a verified viewport
--     attach via render.PanelAdded).
--
--   RENDER SELECTOR: exactly one surface is visible at a time. If the rich
--     panel is live AND verifiably attached, render it and Collapse the
--     watermark; otherwise reparent + render the one-line watermark.
--     apply_visibility + release_ui_capture drive whichever surface is active
--     with the SAME HitTestInvisible rule.
--
-- ====================================================================
-- v0.6.0 HYBRID ARCHITECTURE (still the transport/input foundation)
-- ====================================================================
-- This release replaces the hand-built UMG widget tree + ImGui input box
-- (v0.5.x) with the PROVEN community "MultiplayerChat" approach, while
-- keeping Beacon's HTTP transport + file-feed pipeline unchanged.
--
--   INPUT  (from Nexus): NO SetInputMode switch — that was the source of
--     the recurring "stuck movement after typing" bug. Instead, on T we
--     FreezeMovement (SetIgnoreMoveInput/LookInput true + MaxWalkSpeed/
--     MaxSwimSpeed=0 + JumpMaxCount=0 + DisableInput) and capture
--     keystrokes via per-key RegisterKeyBind into a Lua TypeBuffer with a
--     CursorPos. On Enter/Esc we UnfreezeMovement (reverse ALL of it).
--     Esc also SetPause(false) so the pause menu never opens.
--
--   RENDER (from Nexus): reparent the game's native WBP_Watermark widget
--     to the viewport and write our chat text into its VersionInfo text
--     block. No hand-built widget tree, no white input box — the typing
--     line is rendered "> text|" with a literal "|" cursor. Positioned
--     nudged right off the far-left edge.
--
--   FADEOUT (from Nexus, tuned): the overlay shows on any activity and
--     auto-hides after 90s of no activity (tuned up from Nexus's 6s).
--
--   TRANSPORT (kept from Beacon): SEND stays queue_outbox() — writes a
--     per-message file in %LOCALAPPDATA%\Solarpunk\client\chat-outbox\ that
--     the .NET launcher POSTs to /api/v1/chat/player. RECEIVE stays
--     refresh_feed/parse_messages reading chat-feed.json (the launcher
--     writes it from the server's /chat/recent — this is how admin
--     broadcasts + MOTD + other players' messages arrive). We do NOT use
--     the Nexus ServerExecRPC/ClientMessage hooks (that would lose admin
--     broadcasts + MOTD and need separate validation).
--
-- ====================================================================
-- CRASH RULE (hard-won from many real native AVs on this exact UE4SS)
-- ====================================================================
-- Passing a Lua TABLE or a bare Lua STRING to a reflected struct/FText
-- argument can cause an UNCATCHABLE native access violation (pcall can't
-- catch it). So:
--   * Text  -> always a REAL FText via make_ftext(s); never a bare string.
--   * Struct PROPERTY writes -> read/modify/write-back of the REAL live
--     sub-structs (color, shadow, font, slot LayoutData). Never a table.
--   * Struct UFunction ARGS  -> avoid in general. We do NOT call
--     SetColorAndOpacity({...}) and similar struct setters.
--     ONE DOCUMENTED EXCEPTION: the UWidget viewport setters
--     SetPositionInViewport({X,Y}) / SetDesiredSizeInViewport({X,Y}) /
--     SetAnchorsInViewport({Min,Max}) / SetAlignmentInViewport({X,Y}) take
--     FVector2D-shaped {X,Y} tables. These are the v0.6.1 exception: the
--     proven Nexus mod ships exactly these calls on this same UE5.6 SN2 +
--     UE4SS build (native FVector2D marshaller), and the slot-LayoutData
--     route silently left the widget centered. They are all pcall-wrapped on
--     the game thread; see position_watermark() + build_rich_panel().
--   * Plain bool/int/float/FName args + UObject ptr args are safe.
--   * Multi-line history (v0.8.2) is enabled by PLAIN SCALAR writes only:
--     AutoWrapText = true (bool) + MinDesiredWidth = float + the no-arg
--     SynchronizeProperties() flush. No table/struct/FText is passed to any
--     reflected call; the "\n"-joined transcript still reaches the widget
--     only through safe_set_text -> make_ftext -> SetText(FText).
--   * Every reflected call is wrapped in pcall AND runs on the game thread.

local MOD_NAME    = "SolarpunkHud"
local MOD_VERSION = "0.1.0"

-- ---- paths / constants (KEEP from Beacon) ----
local APPDATA       = os.getenv("LOCALAPPDATA") or ""
local CLIENT_DIR    = APPDATA .. [[\Solarpunk\client]]
local FEED_PATH     = CLIENT_DIR .. [[\chat-feed.json]]
local MOTD_PATH     = CLIENT_DIR .. [[\chat-motd.txt]]
local SERVER_PATH   = CLIENT_DIR .. [[\beacon-current-server.json]]
local OUTBOX_DIR    = CLIENT_DIR .. [[\chat-outbox]]
local LOG_PATH      = APPDATA .. [[\Solarpunk\solarpunk-hud.log]]   -- diagnostic/debug log
local CHAT_LOG_PATH = APPDATA .. [[\Solarpunk\chat.log]]         -- clean chat transcript

local MAX_MESSAGES        = 20        -- ring cap for the on-screen overlay
local POLL_INTERVAL_MS    = 1000      -- file recheck cadence
local MOTD_BANNER_SECONDS = 12        -- how long a fresh MOTD pulses
local CHAT_INPUT_CAP      = 200       -- bytes — comfortable single-thought cap; server caps body at 512

local ROAMING      = os.getenv("APPDATA") or ""
local DEBUG_FILE   = ROAMING .. [[\Solarpunk\client-debug.txt]]       -- shared marker, written by the launcher
local CHAT_OFF_FILE = ROAMING .. [[\Solarpunk\client-chat-off.txt]]   -- toggle: if present, the overlay is hidden
local PLAYER_FILE  = ROAMING .. [[\Solarpunk\client-player.txt]]      -- local player display name (for echo match)
-- v0.8.3: the chat-open key is configurable. Default ENTER (proven, never a
-- bound SN2 letter action). Drop this file by hand containing a single key
-- token to override, e.g. "Y" or "ENTER" or "NUM_ENTER". Read ONCE at load.
local OPEN_KEY_FILE = ROAMING .. [[\Solarpunk\client-chat-open-key.txt]]
-- v0.8.1 brick-proofing: the rich StaticConstructObject panel is an
-- uncatchable native-paint AV risk on this exact UE4SS/UE5.6/D3D12 build, so
-- it is OPT-IN. Absent (the default) => watermark-only, ZERO panel exposure.
-- Drop this file by hand containing "1"/"on"/"true" to enable the rich panel
-- for in-game validation (mirrors the DEBUG flag). The launcher does NOT
-- write it, so the shipped default can never brick.
local PANEL_FLAG_FILE = ROAMING .. [[\Solarpunk\client-rich-panel.txt]]

-- v0.8.4 BISECTION DIAGNOSTICS — movement is dead from LOAD (before any typing),
-- so the cause is the mod's load-time footprint. Two opt-in markers isolate it:
--   client-bh-norender.txt -> skip the WBP_Watermark reparent / AddToViewport entirely.
--   client-bh-nokeys.txt   -> skip registering the per-key (A-Z / number / punct) capture binds.
-- Marker present = that subsystem is DISABLED. Find which one frees WASD/mouse.
local function _diag_marker(name)
    local f = io.open(ROAMING .. [[\Solarpunk\]] .. name, "r")
    if f then f:close(); return true end
    return false
end
local DIAG_NORENDER = _diag_marker("client-bh-norender.txt")
local DIAG_NOKEYS   = _diag_marker("client-bh-nokeys.txt")

-- ---- v0.6.0 render/input tunables ----
local FADEOUT_TIME = 90.0    -- seconds of no activity before the overlay auto-hides
local POSITION_X   = 48.0    -- viewport px from the LEFT edge (nudged right off the far-left bezel)
local POSITION_Y   = 200.0   -- viewport px from the TOP edge (Nexus value)
local WIDGET_W     = 500.0   -- chat column width
local WIDGET_H     = 400.0   -- chat column height
local CHAT_WRAP_COLS = 72    -- v0.8.7: Lua-side wrap column count (native WrapTextAt doesn't hold on the reparented watermark; this guarantees the bound)
local Z_ORDER      = 50      -- above the HUD, click-through (HitTestInvisible)

-- ---- v0.8.0 rich-panel tunables ----
local PANEL_X            = 40.0    -- viewport px from LEFT
local PANEL_Y            = 160.0   -- viewport px from TOP
local PANEL_W            = 460.0   -- panel width
local PANEL_H            = 320.0   -- panel height
local FEED_BODY_WRAP_AT  = 420.0   -- wrap the white body text inside a row
local MAX_FEED_ROWS      = MAX_MESSAGES + 4   -- fixed pool of prebuilt rows (last-N window)
-- v0.8.1: the rich panel is TRY-ONCE (no retry/cooldown loop), so the old
-- PANEL_RETRY_SECS / PANEL_MAX_ATTEMPTS backoff knobs were removed.

-- ESlateVisibility ints (avoid passing enum names through reflection).
local VIS_COLLAPSED           = 1
local VIS_HIT_TEST_INVISIBLE  = 3

-- ESlateBrushDrawType: 1 = Box, 4 = RoundedBox (UE5).
local BRUSH_DRAW_BOX          = 1
local BRUSH_DRAW_ROUNDED_BOX  = 4

-- Beacon debug flag. Off by default so the in-game console stays neat; the
-- launcher writes "1" for dev builds. Shared with BeaconConnect.
local DEBUG = (function()
    local f = io.open(DEBUG_FILE, "r")
    if not f then return false end
    local v = (f:read("*all") or ""):gsub("%s+", ""):lower()
    f:close()
    return v == "1" or v == "on" or v == "true"
end)()

-- v0.8.1: the rich panel is DEFAULT-OFF and only enabled by this opt-in
-- marker. Read ONCE at load; never re-read per tick (the brick-proof default
-- is a hard, stable decision, not a runtime toggle). When false, the panel
-- path is never even attempted, so there is zero native-paint-AV exposure.
local PANEL_ENABLED = (function()
    local f = io.open(PANEL_FLAG_FILE, "r")
    if not f then return false end
    local v = (f:read("*all") or ""):gsub("%s+", ""):lower()
    f:close()
    return v == "1" or v == "on" or v == "true"
end)()

-- v0.8.3: configurable chat-open key (default ENTER). Read ONCE at load. The
-- token is normalized to an UPPER-CASE Key-table name. ENTER + NUM_ENTER are
-- always wired (the historical default); a configured A-Z letter token ALSO
-- opens chat when not typing. Whatever the open key is, it is CONSUMED (the
-- bind returns true) only when chat actually opens, so it can never also fire a
-- game action; when it does not open it passes straight through.
local OPEN_KEY_NAME = (function()
    local f = io.open(OPEN_KEY_FILE, "r")
    if not f then return "ENTER" end
    local v = (f:read("*all") or ""):gsub("%s+", ""):upper()
    f:close()
    if v == "" then return "ENTER" end
    -- accept only a constrained allowlist: ENTER / NUM_ENTER / a single A-Z.
    if v == "ENTER" or v == "RETURN" then return "ENTER" end
    if v == "NUM_ENTER" or v == "NUMPADENTER" then return "NUM_ENTER" end
    if #v == 1 and v:match("^[A-Z]$") then return v end
    return "ENTER"   -- anything unrecognized falls back to the proven default
end)()
-- Is the configured open key a single letter (A-Z)? If so the letter binds
-- below open chat on that key when not typing; otherwise ENTER is the opener.
local OPEN_KEY_LETTER = (#OPEN_KEY_NAME == 1) and OPEN_KEY_NAME or nil

print("===================================================")
print(string.format("  SOLARPUNK HUD  v%s          debug: %s", MOD_VERSION, DEBUG and "ON" or "off"))
print("  in-game chat overlay - Solarpunk")
print(string.format("  rich panel: %s (default watermark fallback)",
    PANEL_ENABLED and "OPT-IN (validation)" or "off"))
print("  status: loaded, watching chat feed")
print("===================================================")

local function log_line(msg)
    pcall(function()
        local f = io.open(LOG_PATH, "a")
        if not f then return end
        f:write(string.format("[%s %s] %s %s\n",
            MOD_NAME, MOD_VERSION, os.date("!%Y-%m-%dT%H:%M:%SZ"), msg))
        f:close()
    end)
end

-- Organized chat transcript — one structured line per message.
local function chat_log(channel, sender, body)
    pcall(function()
        local f = io.open(CHAT_LOG_PATH, "a")
        if not f then return end
        local ch  = (channel and channel ~= "") and channel:upper() or "CHAT"
        local who = (sender and sender ~= "") and (sender .. ": ") or ""
        f:write(string.format("[%s] [%s] %s%s\n",
            os.date("!%Y-%m-%dT%H:%M:%SZ"), ch, who, body or ""))
        f:close()
    end)
end

log_line("loaded; watching " .. FEED_PATH)
log_line(string.format("DIAG: norender=%s nokeys=%s", tostring(DIAG_NORENDER), tostring(DIAG_NOKEYS)))
log_line(PANEL_ENABLED
    and "rich panel OPT-IN via marker; try-once, forever-disable-on-failure, watermark fallback"
    or  "rich panel DISABLED by default (brick-proof); watermark-only render")
log_line(string.format("chat-open key = %s%s (ENTER + NUM_ENTER always wired)",
    OPEN_KEY_NAME, OPEN_KEY_LETTER and " [letter opener]" or ""))

local function activity(msg)
    print(string.format("[Solarpunk %s] %s", MOD_VERSION, msg))
    log_line(msg)
end
local function dbg(msg)
    if DEBUG then print("[Beacon dbg] " .. msg) end
    log_line(msg)
end

-- =====================================================================
-- File IO helpers (KEEP from Beacon)
-- =====================================================================
local function read_all(path)
    local f = io.open(path, "rb")
    if not f then return nil end
    local body = f:read("*all")
    f:close()
    return body
end

local function file_mtime(path)
    -- io can't stat — peek length + first/last byte and treat any change
    -- as a change. Cheap and works on every Lua build UE4SS ships.
    local body = read_all(path)
    if not body then return nil end
    return #body, body:sub(1, 1), body:sub(-1)
end

-- =====================================================================
-- Tiny JSON-ish parser (KEEP from Beacon) — we control the writer
-- (ChatFeedService.cs) so the input is always well-formed.
-- =====================================================================
local function unquote(s)
    s = s:gsub('\\"', '"'):gsub('\\\\', '\\'):gsub('\\/', '/')
    s = s:gsub('\\n', '\n'):gsub('\\r', '\r'):gsub('\\t', '\t')
    return s
end

local function json_escape(s)
    s = tostring(s)
    s = s:gsub('\\', '\\\\')
         :gsub('"', '\\"')
         :gsub('\b', '\\b')
         :gsub('\f', '\\f')
         :gsub('\n', '\\n')
         :gsub('\r', '\\r')
         :gsub('\t', '\\t')
    return s
end

local function parse_messages(body)
    local out = {}
    if not body then return out end
    local arr = body:match('"messages"%s*:%s*%[(.-)%]%s*}?%s*$')
              or body:match('"messages"%s*:%s*%[(.-)%]')
    if not arr or arr == "" then return out end

    local depth, start = 0, nil
    for i = 1, #arr do
        local c = arr:sub(i, i)
        if c == "{" then
            if depth == 0 then start = i end
            depth = depth + 1
        elseif c == "}" then
            depth = depth - 1
            if depth == 0 and start then
                local obj = arr:sub(start, i)
                local msg = {}
                msg.ts      = tonumber(obj:match('"ts"%s*:%s*(%-?%d+)')) or 0
                msg.channel = obj:match('"channel"%s*:%s*"(.-[^\\])"') or ""
                msg.sender  = obj:match('"sender"%s*:%s*"(.-[^\\])"')
                -- body field was fixed to read "msg" upstream; chat-feed.json
                -- uses "body" as the JSON key, so keep matching "body".
                msg.body    = obj:match('"body"%s*:%s*"(.-[^\\])"') or ""
                msg.color   = obj:match('"color"%s*:%s*"(.-[^\\])"')
                if msg.sender then msg.sender = unquote(msg.sender) end
                msg.body = unquote(msg.body)
                out[#out + 1] = msg
                start = nil
            end
        end
    end
    return out
end

-- =====================================================================
-- State (transport/feed — KEEP from Beacon)
-- =====================================================================
local feed_state   = { sig = nil, messages = {} }
local motd_state   = { sig = nil, text = "", shown_at = 0 }
local server_state = { sig = nil, name = "(disconnected)", endpoint = "" }
local seen_signatures = {}    -- ts:body keys; dedupe console-print

local outbox_seq   = 0
local last_send_log = 0

-- LOCAL ECHO (KEEP from Beacon v0.5.9): a player's own sent line appears
-- INSTANTLY, not after the ~2-4s server round-trip. Rendered "pending"
-- below the server feed until the server bounces the same body back
-- (confirmed) or a 7s timeout elapses.
local pending_echoes = {}

local function player_name()
    local f = io.open(PLAYER_FILE, "r"); if not f then return nil end
    local v = f:read("*all") or ""; f:close()
    v = v:gsub("^%s+", ""):gsub("%s+$", "")
    if v == "" then return nil end
    return v
end

-- Forward declaration so add_echo / refresh_feed can bump the render state
-- before the render table is defined further down.
local render       -- the render state (defined below)
local render_tick  -- the surface selector (assigned with the rich-panel block)
local function mark_activity()  -- bump LastActivity + force a repaint
    if render then
        render.LastActivity = os.clock()
        render.dirty = true
    end
end

local function add_echo(body)
    pending_echoes[#pending_echoes + 1] = { body = body, at = os.time(), sender = player_name() }
    mark_activity()   -- show my own line immediately + reset the fadeout timer
end

local function prune_echoes()
    if #pending_echoes == 0 then return end
    local present = {}
    for _, m in ipairs(feed_state.messages) do
        if m.body and m.body ~= "" then present[m.body] = true end
    end
    local now = os.time()
    local kept = {}
    for _, e in ipairs(pending_echoes) do
        if not present[e.body] and (now - e.at) < 7 then kept[#kept + 1] = e end
    end
    pending_echoes = kept
end

local function message_key(m) return tostring(m.ts) .. "|" .. (m.body or "") end

local function ingest_feed_body(body)
    local parsed = parse_messages(body)
    local had_new = false
    for _, m in ipairs(parsed) do
        local k = message_key(m)
        if not seen_signatures[k] then
            seen_signatures[k] = true
            had_new = true
            local sender_str = m.sender and ("<" .. m.sender .. "> ") or ""
            print(string.format("[Beacon chat] %s%s", sender_str, m.body))   -- UE4SS console
            log_line(string.format("chat %s %s%s", m.channel or "", sender_str, m.body))
            chat_log(m.channel, m.sender, m.body)                            -- transcript
        end
    end
    -- Cap the on-screen window to the most recent N.
    if #parsed > MAX_MESSAGES then
        local trimmed = {}
        for i = #parsed - MAX_MESSAGES + 1, #parsed do trimmed[#trimmed + 1] = parsed[i] end
        feed_state.messages = trimmed
    else
        feed_state.messages = parsed
    end
    -- v0.6.0: any NEW incoming message (other players + admin broadcasts +
    -- MOTD-as-message) bumps LastActivity so the overlay shows + resets fade.
    if had_new then mark_activity() end
end

local function refresh_feed()
    local len, first, last = file_mtime(FEED_PATH)
    if not len then return end
    local sig = string.format("%d|%s|%s", len, first or "", last or "")
    if sig == feed_state.sig then return end
    feed_state.sig = sig
    local body = read_all(FEED_PATH)
    if body then ingest_feed_body(body) end
end

local function refresh_motd()
    local len, first, last = file_mtime(MOTD_PATH)
    if not len then return end
    local sig = string.format("%d|%s|%s", len, first or "", last or "")
    if sig == motd_state.sig then return end
    motd_state.sig = sig
    local body = read_all(MOTD_PATH) or ""
    if body ~= motd_state.text then
        motd_state.text = body
        motd_state.shown_at = os.time()
        if body ~= "" then
            print(string.format("[%s] MOTD: %s", MOD_NAME, body))
            log_line("motd " .. body)
            mark_activity()   -- a fresh MOTD pops the overlay too
        end
    end
end

local function refresh_server()
    local len, first, last = file_mtime(SERVER_PATH)
    if not len then
        if server_state.name ~= "(disconnected)" then
            server_state.name = "(disconnected)"
            server_state.endpoint = ""
        end
        return
    end
    local sig = string.format("%d|%s|%s", len, first or "", last or "")
    if sig == server_state.sig then return end
    server_state.sig = sig
    local body = read_all(SERVER_PATH) or ""
    server_state.name     = body:match('"server_name"%s*:%s*"(.-[^\\])"') or "(unknown)"
    server_state.endpoint = body:match('"endpoint_url"%s*:%s*"(.-[^\\])"') or ""
    activity("server: " .. server_state.name)
    dbg("endpoint " .. server_state.endpoint)
end

-- =====================================================================
-- Outbox writer (KEEP from Beacon) — the HTTP SEND path.
-- =====================================================================
local function queue_outbox(body)
    if type(body) ~= "string" then return false end
    body = body:gsub("^%s+", ""):gsub("%s+$", "")
    if body == "" then return false end
    if #body > CHAT_INPUT_CAP then body = body:sub(1, CHAT_INPUT_CAP) end

    outbox_seq = (outbox_seq + 1) % 100000
    local now_sec = os.time()
    local now_ms = now_sec * 1000
    local filename = string.format("%013d-%05d.json", now_ms, outbox_seq)
    local path = OUTBOX_DIR .. [[\]] .. filename
    local tmp = path .. ".tmp"

    local payload = string.format(
        '{"version":1,"ts":%d,"body":"%s"}',
        now_sec, json_escape(body))

    local f = io.open(tmp, "wb")
    if not f then
        log_line("outbox open failed for " .. tmp)
        return false
    end
    f:write(payload)
    f:close()
    local ok = os.rename(tmp, path)
    if not ok then
        local g = io.open(path, "wb")
        if g then g:write(payload); g:close() else
            log_line("outbox rename + fallback both failed for " .. path)
            return false
        end
        pcall(os.remove, tmp)
    end
    last_send_log = now_sec
    log_line("outbox queued " .. filename .. " body=" .. body)
    add_echo(body)   -- show my own line instantly; pruned when the server bounce arrives
    return true
end

-- =====================================================================
-- Reflection helpers (KEEP from Beacon — crash-safe foundations)
-- =====================================================================
local function run_on_game_thread(fn)
    if type(ExecuteInGameThread) == "function" then
        return pcall(function() ExecuteInGameThread(fn) end)
    end
    return pcall(fn)
end

local function obj_valid(o)
    if o == nil then return false end
    local ok, v = pcall(function() return o:IsValid() end)
    return ok and v == true
end

-- Construct a real FText from a Lua string. SetText(plain_string) native-
-- crashes on this build, so we hand SetText an actual FText object. The
-- ctor routes are pcall-guarded Lua (a missing method is a catchable Lua
-- error, not a native crash).
local function make_ftext(s)
    local ft
    local oka = pcall(function() ft = FText(s) end)              -- callable userdata / fn
    if oka and ft ~= nil then return ft end
    local okb = pcall(function() ft = FText.FromString(s) end)   -- static method
    if okb and ft ~= nil then return ft end
    local okc = pcall(function() ft = FText.new(s) end)          -- .new
    if okc and ft ~= nil then return ft end
    return nil
end

-- SetText safely: build a real FText, then the one risky reflected call.
local function safe_set_text(textWidget, s)
    if not obj_valid(textWidget) then return false end
    local ft = make_ftext(s)
    if ft == nil then return false end
    return pcall(function() textWidget:SetText(ft) end)
end

local function GetPC()
    local pc = nil
    pcall(function() pc = FindFirstOf("PlayerController") end)
    if obj_valid(pc) then return pc end
    return nil
end

-- Pawn accessor that tolerates either .Pawn / .Character / GetPawn().
local function GetPawn(pc)
    pc = pc or GetPC()
    if not obj_valid(pc) then return nil end
    local pawn = nil
    pcall(function() pawn = pc:GetPawn() end)
    if not obj_valid(pawn) then pcall(function() pawn = pc.Pawn end) end
    if not obj_valid(pawn) then pcall(function() pawn = pc.Character end) end
    if obj_valid(pawn) then return pawn end
    return nil
end

-- Movement in the proven mod is controlled through pc.Character, not the
-- generic pawn accessor. Prefer that exact route, then fall back for safety.
local function GetMovementPawn(pc)
    pc = pc or GetPC()
    if not obj_valid(pc) then return nil, "none" end
    local pawn = nil
    pcall(function() pawn = pc.Character end)
    if obj_valid(pawn) then return pawn, "Character" end
    pcall(function() pawn = pc.Pawn end)
    if obj_valid(pawn) then return pawn, "Pawn" end
    pcall(function() pawn = pc:GetPawn() end)
    if obj_valid(pawn) then return pawn, "GetPawn" end
    return nil, "none"
end

local function IsInGame()
    local pc = GetPC()
    if not pc then return false end
    return GetPawn(pc) ~= nil
end

-- =====================================================================
-- Movement Lock (from the proven Nexus mod, with zero-cache protection).
-- NO SetInputMode switch anywhere. Originals are only refreshed from sane
-- positive values, so a previously stuck MaxWalkSpeed=0 cannot poison the
-- next restore.
-- =====================================================================
local frozen = {
    active    = false,
    walkSpeed = 400.0,
    swimSpeed = 400.0,
    jumpCount = 1,
}

local function object_name(o)
    if not obj_valid(o) then return "nil" end
    local name = nil
    pcall(function() name = o:GetFullName() end)
    if name == nil then pcall(function() name = o:GetName() end) end
    return tostring(name or o)
end

local function read_pc_input_state(pc)
    local move = "n/a"
    local look = "n/a"
    local cursor = "n/a"
    pcall(function() move = tostring(pc:GetIgnoreMoveInput()) end)
    if move == "n/a" then pcall(function() move = tostring(pc:IsMoveInputIgnored()) end) end
    pcall(function() look = tostring(pc:GetIgnoreLookInput()) end)
    if look == "n/a" then pcall(function() look = tostring(pc:IsLookInputIgnored()) end) end
    pcall(function() cursor = tostring(pc.bShowMouseCursor) end)
    return move, look, cursor
end

local function read_movement_values(pawn)
    local walk = "nil"
    local swim = "nil"
    local jump = "nil"
    local cmc = nil
    if obj_valid(pawn) then
        pcall(function()
            cmc = pawn.CharacterMovement
            if cmc then
                walk = tostring(cmc.MaxWalkSpeed)
                swim = tostring(cmc.MaxSwimSpeed)
            end
        end)
        pcall(function() jump = tostring(pawn.JumpMaxCount) end)
    end
    return cmc, walk, swim, jump
end

local function log_movement_state(phase, pc, pawn, pawnSource, extra)
    local move, look, cursor = read_pc_input_state(pc)
    local _, walk, swim, jump = read_movement_values(pawn)
    log_line(string.format(
        "%s pc=%s pawnSource=%s pawn=%s ignoreMove=%s ignoreLook=%s cursor=%s walk=%s swim=%s jump=%s%s",
        phase,
        object_name(pc),
        tostring(pawnSource or "none"),
        object_name(pawn),
        move,
        look,
        cursor,
        walk,
        swim,
        jump,
        extra and (" " .. extra) or ""))
end

local function cache_positive_speed(field, value)
    if type(value) == "number" and value > 0 then
        frozen[field] = value
    end
end

local function restore_speed(field, fallback)
    local value = frozen[field]
    if type(value) ~= "number" or value <= 0 then return fallback end
    return value
end

local function FreezeMovement()
    local pc = GetPC()
    if not pc then return end

    local pawn, pawnSource = GetMovementPawn(pc)
    log_movement_state("movement freeze before", pc, pawn, pawnSource)

    pcall(function() pc:SetIgnoreMoveInput(true) end)
    pcall(function() pc:SetIgnoreLookInput(true) end)

    if pawn then
        pcall(function()
            local cmc = pawn.CharacterMovement
            if cmc then
                cache_positive_speed("walkSpeed", cmc.MaxWalkSpeed)
                cache_positive_speed("swimSpeed", cmc.MaxSwimSpeed)
                cmc.MaxWalkSpeed = 0
                cmc.MaxSwimSpeed = 0
            end
        end)
        pcall(function()
            if type(pawn.JumpMaxCount) == "number" and pawn.JumpMaxCount > 0 then
                frozen.jumpCount = pawn.JumpMaxCount
            end
            pawn.JumpMaxCount = 0
        end)
        pcall(function() pawn:DisableInput(pc) end)
    end
    frozen.active = true
    log_movement_state(
        "movement freeze after",
        pc,
        pawn,
        pawnSource,
        string.format("restoreWalk=%s restoreSwim=%s restoreJump=%s",
            tostring(frozen.walkSpeed), tostring(frozen.swimSpeed), tostring(frozen.jumpCount)))
end

local function UnfreezeMovement()
    local pc = GetPC()
    if not pc then frozen.active = false; return end

    local pawn, pawnSource = GetMovementPawn(pc)
    log_movement_state("movement unfreeze before", pc, pawn, pawnSource)

    pcall(function() pc:SetIgnoreMoveInput(false) end)
    pcall(function() pc:ResetIgnoreMoveInput() end)   -- zero the counter outright
    pcall(function() pc:SetIgnoreLookInput(false) end)
    pcall(function() pc:ResetIgnoreLookInput() end)

    if pawn then
        pcall(function()
            local cmc = pawn.CharacterMovement
            if cmc then
                cmc.MaxWalkSpeed = restore_speed("walkSpeed", 400.0)
                cmc.MaxSwimSpeed = restore_speed("swimSpeed", 400.0)
            end
        end)
        pcall(function() pawn.JumpMaxCount = restore_speed("jumpCount", 1) end)
        pcall(function() pawn:EnableInput(pc) end)
    end
    -- Clear any latched key state so movement resumes the same frame.
    pcall(function() pc:FlushPressedKeys() end)
    frozen.active = false
    log_movement_state(
        "movement unfreeze after",
        pc,
        pawn,
        pawnSource,
        string.format("restoredWalk=%s restoredSwim=%s restoredJump=%s",
            tostring(restore_speed("walkSpeed", 400.0)),
            tostring(restore_speed("swimSpeed", 400.0)),
            tostring(restore_speed("jumpCount", 1))))
end

-- =====================================================================
-- Render state. Two surfaces share this table:
--   * watermark fallback (always available, one-line, SHIPS BY DEFAULT):
--       Watermark/VersionInfo
--   * rich navy-glass panel (OPT-IN only, scrollable): PanelRoot/Feed/...
-- v0.8.1 brick-proofing: the panel is DEFAULT-DISABLED (panelDisabled starts
-- true unless the opt-in marker enabled it) and TRY-ONCE (panelTried gates it
-- to a single attempt; any failure/invalidation re-arms panelDisabled). It can
-- never be attempted per-tick, never retry-spam, never starve the game thread.
-- =====================================================================
render = {
    -- watermark fallback
    Watermark    = nil,    -- the reparented WBP_Watermark widget
    VersionInfo  = nil,    -- its child text block we write chat into
    Reparented   = false,
    StyleApplied = false,  -- style/Slate sync runs once per live widget
    WmVisible    = -1,     -- last-applied watermark visibility int
    lastText     = nil,    -- last watermark string pushed
    -- v0.8.2 multi-line history (try-once on the live VersionInfo, like
    -- StyleApplied). MultiLineReady re-arms to false whenever a fresh
    -- VersionInfo is rebound (map travel) so the flip is re-attempted once per
    -- live widget; MultiLineDisabled latches true forever on any failure so the
    -- single-line render owns the screen with no retry-spam.
    MultiLineReady    = false,
    MultiLineDisabled = false,

    -- rich panel
    PanelRoot    = nil,    -- root UserWidget
    PanelTree    = nil,    -- its WidgetTree (outer for child construction)
    PanelAdded   = false,  -- v0.8.1: true ONLY after a VERIFIED viewport attach
    Border       = nil,
    Header       = nil,
    Scroll       = nil,    -- the ScrollBox (nil if last-N fallback path)
    Feed         = nil,    -- the VerticalBox of message rows
    FeedRows     = {},     -- prebuilt fixed-N row widget pool
    Input        = nil,    -- the typing-line TextBlock
    PanelVisible = -1,     -- last-applied panel visibility int
    headerSet    = false,
    lastFeedSig  = nil,
    lastInput    = nil,
    building     = false,  -- re-entry guard (cross-thread double-build)

    -- try-once guard
    panelTried    = false, -- set true on the single allowed attempt
    panelAttempts = 0,     -- bookkeeping only (logged on failure)
    panelFailedAt = 0,     -- os.clock() of the failed attempt (logged)
    -- v0.8.1: DEFAULT-DISABLED unless the opt-in marker is present. When the
    -- panel is opted in, it is still TRY-ONCE: ANY failure or post-success
    -- invalidation flips this true forever (watermark takes over for good).
    panelDisabled = (not PANEL_ENABLED),

    -- shared
    LastActivity = 0,
    dirty        = true,   -- force a text repaint next render tick
    bootTime     = os.clock(),
}

-- Typing state (ADOPT from Nexus).
local typing = {
    active     = false,
    buffer     = "",
    cursor     = 0,        -- 0 = start, #buffer = end
    sent       = {},       -- sent-history for Up/Down recall
    sentIdx    = 0,        -- 0 = not browsing
    lastKeyAt  = 0,        -- os.clock() of the last typing keystroke — drives the
                           -- 15s frozen-movement failsafe. Must be typing-local:
                           -- render.LastActivity is also bumped by INCOMING
                           -- messages, which kept the failsafe from ever firing
                           -- on a busy server (stuck-open box = frozen forever).
}

-- =====================================================================
-- Watermark reparent (ADOPT from Nexus, made crash-safe per Beacon rules).
-- Find -> reparent -> position via slot LayoutData -> hide stub -> seed.
-- Idempotent + self-healing: re-runs after map travel destroys the widget.
-- MUST run on the game thread (callers wrap in run_on_game_thread).
-- =====================================================================
local function position_watermark(wm)
    -- v0.6.1: position with the UWidget viewport setters — the SAME calls the
    -- PROVEN Nexus mod uses. The v0.6.0 slot-LayoutData approach silently
    -- failed and left the widget centered. These {X,Y}/{Min,Max} table args
    -- are the one place tables are tolerated: the Nexus mod ships with exactly
    -- these calls on this same UE5.6 SN2 + UE4SS build, so they marshal safely.
    -- Top-left anchor/alignment -> PositionX/Y are absolute px from top-left.
    pcall(function() wm:SetPositionInViewport({ X = POSITION_X, Y = POSITION_Y }, false) end)
    pcall(function() wm:SetDesiredSizeInViewport({ X = WIDGET_W, Y = WIDGET_H }) end)
    pcall(function() wm:SetAnchorsInViewport({ Minimum = { X = 0, Y = 0 }, Maximum = { X = 0, Y = 0 } }) end)
    pcall(function() wm:SetAlignmentInViewport({ X = 0, Y = 0 }) end)
end

-- v0.8.1: crash-safe CDO test. FindAllOf returns the class-default-object,
-- which must NEVER be mutated/reparented. Read HasAnyFlags ONCE inside a single
-- pcall and store the boolean (the v0.7.2 code called it twice — once in pcall,
-- once UNGUARDED — which both double-reflected and, on a build where the call
-- throws, crossed an uncatchable call off the pcall'd path). FAIL CLOSED: if we
-- cannot prove it is NOT a CDO, treat it AS a CDO and skip it (a false "is CDO"
-- only costs a no-op pass; mutating a real CDO is a hard crash).
local function hud_is_cdo(hud)
    local ok, flagged = pcall(function()
        return hud:HasAnyFlags(EObjectFlags.RF_ClassDefaultObject)
    end)
    if ok and type(flagged) == "boolean" then return flagged end
    return true   -- fail closed
end

local function reparent_watermark()
    if DIAG_NORENDER then return nil end   -- bisection: skip reparent/AddToViewport entirely
    if render.Reparented and obj_valid(render.Watermark) and obj_valid(render.VersionInfo) then
        return render.Watermark
    end
    -- A stale pointer after map travel: drop it and re-find.
    render.Reparented  = false
    render.Watermark   = nil
    render.VersionInfo = nil
    render.StyleApplied = false
    render.MultiLineReady = false      -- re-attempt the multi-line flip on the fresh widget
    render.MultiLineDisabled = false   -- re-arm the disable latch so a fresh widget retries (mirrors StyleApplied)

    -- SOLARPUNK REPARENT TARGET (port note):
    -- SN2's BeaconHud reparented WBP_TopHud_C -> WBP_Watermark -> VersionInfo.
    -- Solarpunk has no watermark widget; the on-box widget enumeration
    -- (run against a live headless host) shows a single always-present
    -- in-game message widget `SW_IngameMessage_C` plus a HUD message stack.
    -- We host the overlay on the first live non-CDO instance of a candidate
    -- host class (ordered below) and render into the FIRST child UTextBlock of
    -- that widget's WidgetTree (discovered generically — we never hardcode a
    -- child name, since the child layout can't be confirmed headless).
    -- The exact host widget + on-screen position MUST be visually confirmed
    -- on a WINDOWED client (-nullrhi can't present UMG); a host override
    -- can be dropped in `%APPDATA%\Solarpunk\client-hud-host.txt` (one class
    -- name, e.g. `SW_IngameMessage_C`). If no candidate is found, this returns
    -- nil and the mod stays alive (chat render disabled, never an AV).
    local host_candidates = { "SW_IngameMessage_C", "SW_QuestDisplay_C",
                              "SW_Compass_C", "W_SaveIndicator_C" }
    do
        local override = nil
        pcall(function()
            local f = io.open(ROAMING .. [[\Solarpunk\client-hud-host.txt]], "r")
            if f then override = (f:read("*l") or ""):gsub("%s+", ""); f:close() end
        end)
        if override and override ~= "" then table.insert(host_candidates, 1, override) end
    end

    -- Find the first child UTextBlock of a host widget's WidgetTree. Generic:
    -- match TextBlock instances whose Outer is the host's WidgetTree. All
    -- pcall'd; returns nil rather than risk an AV on an exotic build.
    local function first_text_child(host)
        local tree = nil
        pcall(function() tree = host.WidgetTree end)
        if not obj_valid(tree) then return nil end
        local tbs = nil
        pcall(function() tbs = FindAllOf("TextBlock") end)
        if not tbs then return nil end
        for _, t in ipairs(tbs) do
            local ok = false
            pcall(function()
                ok = obj_valid(t) and obj_valid(t:GetOuter()) and t:GetOuter() == tree
            end)
            if ok then return t end
        end
        return nil
    end

    for _, cls in ipairs(host_candidates) do
        local hosts = nil
        pcall(function() hosts = FindAllOf(cls) end)
        if hosts then
            for _, host in ipairs(hosts) do
                if obj_valid(host) and not hud_is_cdo(host) then
                    local vi = first_text_child(host)
                    if obj_valid(vi) then
                        pcall(function() host:RemoveFromParent() end)
                        pcall(function() host:AddToViewport(Z_ORDER) end)   -- single int z-order, safe
                        position_watermark(host)
                        safe_set_text(vi, "")

                        render.Watermark   = host
                        render.VersionInfo = vi
                        render.Reparented  = true
                        render.StyleApplied = false
                        render.MultiLineReady = false
                        render.MultiLineDisabled = false
                        activity("chat overlay ready (press Enter to chat)")
                        log_line("reparent: host=" .. cls .. " text-child bound + positioned")
                        return host
                    end
                end
            end
        end
    end
    return nil
end

-- Style the VersionInfo text block (read/modify/write-back ONLY). This is
-- intentionally applied once per live widget; SynchronizeProperties() every
-- render pass forces Slate work and made typing visibly laggy.
local function style_versioninfo(vi)
    -- color: soft white
    pcall(function()
        local c = vi.ColorAndOpacity
        local sc = c.SpecifiedColor
        sc.R = 0.86; sc.G = 0.94; sc.B = 0.98; sc.A = 1.0   -- cool off-white, faint cyan lift — fits SN2, less glare on the bright surface
        c.SpecifiedColor = sc
        vi.ColorAndOpacity = c
    end)
    -- near-opaque dark drop shadow carries the light text over bright/busy scenes
    pcall(function()
        local o = vi.ShadowOffset; o.X = 1.5; o.Y = 1.5; vi.ShadowOffset = o
    end)
    pcall(function()
        local c = vi.ShadowColorAndOpacity
        c.R = 0.0; c.G = 0.01; c.B = 0.03; c.A = 0.95
        vi.ShadowColorAndOpacity = c
    end)
    pcall(function() vi:SetOpacity(1.0) end)   -- single float, safe
    -- Flush the cached style sub-structs into live Slate (no-arg, zero marshal risk)
    -- so the color/shadow take even before the next SetText forces a rebuild.
    pcall(function() vi:SynchronizeProperties() end)
end

-- =====================================================================
-- Build the chat display strings.
--   build_typing_line()    -> "> before|after" with a literal "|" cursor.
--   build_watermark_line() -> the ONE line the single-line VersionInfo block
--                             can actually render (typing line, else newest
--                             message, else MOTD, else a prompt). This is the
--                             v0.8.0 msg-display fix: the watermark block does
--                             NOT render embedded "\n", so we never feed it a
--                             multi-line transcript.
-- =====================================================================
-- Strip stray control chars so a single-line block never gets a raw \r\n\t
-- that it would collapse/clip (must-fix: MOTD/admin text + parser artifacts).
-- The rich-panel path has its own one_line() further down; this is the local
-- sibling for the watermark builders defined here.
local function flatten_line(s)
    s = tostring(s or "")
    return s:gsub("[\r\n\t]", " "):gsub("^%s+", ""):gsub("%s+$", "")
end

-- v0.8.5: per-row formatting. The single shared text block can't colour per
-- line, so system/admin/MOTD lines are distinguished IN-TEXT by a [label]
-- (dropping the "Server" sender so it never reads as a player) and every row
-- gets a [HH:MM] stamp. Pure string work — no reflection, no crash surface.
local CHANNEL_LABEL = {
    system = "[Server]",
    admin  = "[Admin]",
    motd   = "[MOTD]",
}
local function stamp(ts)
    if not ts or ts == 0 then return "" end
    local ok, s = pcall(os.date, "[%H:%M] ", ts)
    return (ok and s) or ""
end
local function format_chat_row(ts, channel, sender, body)
    local label = CHANNEL_LABEL[channel]
    if label then
        return stamp(ts) .. label .. " " .. (body or "")
    end
    local prefix = sender and (sender .. ": ") or ""
    return stamp(ts) .. prefix .. (body or "")
end

local function build_typing_line()
    local before = string.sub(typing.buffer, 1, typing.cursor)
    local after  = string.sub(typing.buffer, typing.cursor + 1)
    local cap = (#typing.buffer >= CHAT_INPUT_CAP) and "  (max)" or ""
    return "> " .. before .. "|" .. after .. cap
end

-- v0.8.9: do NOT line-wrap in Lua. The old wrap_text double-wrapped against the
-- native AutoWrapText (mine broke at 72, the native re-broke at the column
-- width, so lines split twice). Instead leave normal text alone and let the
-- native block wrap it ONCE, cleanly, at the column width; only break a single
-- OVER-LONG unbroken run (no spaces) so the native wrap has a seam to break on.
-- Returns a string (one logical line per message).
local LONG_WORD_CAP = 40
local function break_long_words(s)
    return (tostring(s or ""):gsub("%S+", function(w)
        if #w <= LONG_WORD_CAP then return w end
        local parts = {}
        while #w > LONG_WORD_CAP do parts[#parts + 1] = w:sub(1, LONG_WORD_CAP); w = w:sub(LONG_WORD_CAP + 1) end
        parts[#parts + 1] = w
        return table.concat(parts, " ")
    end))
end

local function newest_feed_line()
    -- newest pending echo wins (it is the most recent thing the player did),
    -- else the newest server feed message, else nothing.
    prune_echoes()
    if #pending_echoes > 0 then
        local e = pending_echoes[#pending_echoes]
        return format_chat_row(e.at, "player", e.sender, e.body or "")
    end
    for i = #feed_state.messages, 1, -1 do
        local m = feed_state.messages[i]
        if m.body and m.body ~= "" then
            return format_chat_row(m.ts, m.channel, m.sender, m.body)
        end
    end
    return nil
end

local function build_watermark_line()
    -- typing line keeps its literal "|" cursor intact (no flatten — it has no
    -- control chars and we never want the cursor mangled).
    if typing.active then return build_typing_line() end
    local newest = newest_feed_line()
    if newest then return flatten_line(newest) end
    if motd_state.text ~= "" and (os.time() - motd_state.shown_at) < MOTD_BANNER_SECONDS then
        return flatten_line(motd_state.text)
    end
    return "Press Enter to chat"
end

-- v0.8.2: multi-line transcript for the AutoWrapText'd VersionInfo block.
-- Sibling of build_watermark_line (NO new reflection): take the last
-- HISTORY_LINES display lines (server feed + pending echoes, oldest->newest),
-- flatten each to one physical line, and "\n"-join them; append the typing
-- line as the FINAL row while typing. Falls back to the exact same prompt
-- build_watermark_line() returns when there is nothing to show yet.
local HISTORY_LINES = 8   -- last-N transcript rows (the typing line is extra)

local function build_history_block()
    prune_echoes()

    -- One ENTRY per message; the native AutoWrapText wraps each at the column
    -- width (a single, clean break). We only pre-break over-long unbroken runs
    -- so the native wrap has a seam. "\n" still separates messages.
    local lines = {}
    for _, m in ipairs(feed_state.messages) do
        if m.body and m.body ~= "" then
            lines[#lines + 1] = break_long_words(flatten_line(format_chat_row(m.ts, m.channel, m.sender, m.body)))
        end
    end
    for _, e in ipairs(pending_echoes) do
        if e.body and e.body ~= "" then
            lines[#lines + 1] = break_long_words(flatten_line(format_chat_row(e.at, "player", e.sender, e.body)))
        end
    end

    -- Keep only the most recent HISTORY_LINES MESSAGES.
    if #lines > HISTORY_LINES then
        local out = {}
        for i = #lines - HISTORY_LINES + 1, #lines do out[#out + 1] = lines[i] end
        lines = out
    end

    -- Nothing yet: defer to the single-line prompt (MOTD pulse / "Press Enter").
    if #lines == 0 and not typing.active then
        return build_watermark_line()
    end

    -- Typing line is the final entry; long input wraps via the native block too.
    if typing.active then
        lines[#lines + 1] = break_long_words(build_typing_line())
    end

    return table.concat(lines, "\n")
end

-- =====================================================================
-- Visibility gate (Beacon rule) + fadeout (Nexus, 90s).
-- in-server only (pawn present) | hidden at main menu | honor off-marker.
-- ESlateVisibility: 0=Visible, 1=Collapsed, 2=Hidden, 3=HitTestInvisible.
-- Use 0 when shown, matching the proven Nexus mod; 1 when hidden.
-- =====================================================================
local chat_off_cached = false
local function refresh_chat_off_marker()
    local f = io.open(CHAT_OFF_FILE, "r")
    local off = false
    if f then f:close(); off = true end
    if off ~= chat_off_cached then
        chat_off_cached = off
        mark_activity()
    end
    return chat_off_cached
end
local function chat_off_marker()
    return chat_off_cached
end
refresh_chat_off_marker()

-- Shared show/hide rule (identical for both surfaces). Returns the
-- ESlateVisibility int to drive the ACTIVE surface with: HitTestInvisible(3)
-- when shown (visible, click-through, never captures input), Collapsed(1)
-- when hidden. NEVER Visible(0) -> the overlay must never eat keyboard/mouse.
local function desired_visibility()
    if typing.active then return VIS_HIT_TEST_INVISIBLE end
    local pc = GetPC()
    local hasPawn = (pc ~= nil) and (GetPawn(pc) ~= nil)
    local cursor = false
    if pc then pcall(function() cursor = pc.bShowMouseCursor end) end
    local now = os.clock()
    local idle = now - render.LastActivity
    local visible = hasPawn and (not cursor) and (idle <= FADEOUT_TIME)
    if chat_off_marker() then visible = false end
    return visible and VIS_HIT_TEST_INVISIBLE or VIS_COLLAPSED
end

-- Forward declaration: panel_valid is defined with the rich-panel block below.
local panel_valid

-- Drive both surfaces so exactly ONE is visible at a time: the active surface
-- follows desired_visibility(); the inactive surface is forced Collapsed.
local function apply_visibility()
    local panelActive = (panel_valid ~= nil) and panel_valid() == true
    local show = desired_visibility()

    if panelActive then
        local root = render.PanelRoot
        if obj_valid(root) and show ~= render.PanelVisible then
            pcall(function() root:SetVisibility(show) end)
            render.PanelVisible = show
        end
        -- Hide the watermark while the panel owns the screen.
        local wm = render.Watermark
        if obj_valid(wm) and render.WmVisible ~= VIS_COLLAPSED then
            pcall(function() wm:SetVisibility(VIS_COLLAPSED) end)
            render.WmVisible = VIS_COLLAPSED
        end
    else
        local wm = render.Watermark
        if obj_valid(wm) and show ~= render.WmVisible then
            pcall(function() wm:SetVisibility(show) end)
            render.WmVisible = show
        end
        -- Hide the panel root if one exists but isn't currently valid/active.
        local root = render.PanelRoot
        if obj_valid(root) and render.PanelVisible ~= VIS_COLLAPSED then
            pcall(function() root:SetVisibility(VIS_COLLAPSED) end)
            render.PanelVisible = VIS_COLLAPSED
        end
    end
end

-- =====================================================================
-- Watermark fallback render: reparent if needed, push the ONE line.
-- Returns true if the watermark surface is live (so the selector can know
-- whether anything got drawn). Does NOT call apply_visibility (the selector
-- owns that). Runs on the game thread.
-- =====================================================================
local function render_watermark_tick()
    local wm = reparent_watermark()
    if not obj_valid(wm) or not obj_valid(render.VersionInfo) then return false end

    -- Style once after a fresh reparent. Do not SynchronizeProperties() on
    -- every render tick or every typed character.
    if not render.StyleApplied then
        style_versioninfo(render.VersionInfo)
        render.StyleApplied = true
    end

    -- v0.8.2: TRY-ONCE flip the live VersionInfo to multi-line (mirrors the
    -- StyleApplied / panelDisabled try-once shape). ALL plain-scalar / no-arg
    -- writes — same crash-safe shape proven in style_versioninfo + the feed-row
    -- AutoWrapText write. AutoWrapText = true + a finite MinDesiredWidth makes a
    -- native UTextBlock honor embedded "\n" and soft-wrap; SynchronizeProperties
    -- flushes it. Any failure latches MultiLineDisabled true forever, so the
    -- proven single-line render owns the screen with no retry-spam.
    if not render.MultiLineDisabled and not render.MultiLineReady then
        local vi = render.VersionInfo
        local ok = pcall(function() vi.AutoWrapText = true end)   -- plain bool (lets the block honor the "\n" of the Lua-wrapped transcript)
        pcall(function() vi.MinDesiredWidth = WIDGET_W end)        -- plain float (min width)
        pcall(function() vi:SynchronizeProperties() end)          -- no-arg flush
        -- v0.8.8: REMOVED the WrapTextAt + WrappingPolicy writes added in v0.8.6.
        -- They never held on the reparented watermark anyway (wrap_text bounds
        -- every row in Lua now) and were the ONLY engine-level writes added since
        -- movement last worked — pulled as the prime suspect for the input freeze.
        if ok then
            render.MultiLineReady = true
            log_line("multiline: VersionInfo set to AutoWrapText — rendering chat history")
        else
            render.MultiLineDisabled = true
            log_line("multiline: AutoWrapText write failed — single-line watermark owns the screen")
        end
    end

    -- Push only when the text changed. When the VersionInfo is multi-line-ready
    -- we render the last-N transcript + typing line ("\n"-joined, one real FText
    -- via safe_set_text); otherwise we fall straight back to the proven single
    -- newest-line render. The "\n"-joined string is just bytes inside one FText.
    if render.dirty or typing.active then
        local txt = (render.MultiLineReady and not render.MultiLineDisabled)
            and build_history_block()
            or  build_watermark_line()
        if txt ~= render.lastText or render.dirty then
            render.lastText = txt
            safe_set_text(render.VersionInfo, txt)
        end
    end
    return true
end

-- =====================================================================
-- Input: typing helpers (ADOPT from Nexus).
-- =====================================================================
local widget_blueprint_library = nil
local function get_widget_blueprint_library()
    if obj_valid(widget_blueprint_library) then return widget_blueprint_library end
    widget_blueprint_library = nil
    if type(StaticFindObject) ~= "function" then return nil end
    local ok, obj = pcall(StaticFindObject, "/Script/UMG.Default__WidgetBlueprintLibrary")
    if ok and obj_valid(obj) then widget_blueprint_library = obj; return obj end
    return nil
end

-- =====================================================================
-- RICH PANEL (v0.8.0): navy-glass, scrollable chat panel.
--
-- CRASH RULE compliance: every child UWidget is constructed with
-- StaticConstructObject_Internal(class, WidgetTree) — NOT tree:ConstructWidget
-- (a C++ template, invisible to reflection, the v0.7.0 nil-return bug).
-- Children are parented via the reflected BlueprintCallable UObject-ptr APIs.
-- Text via make_ftext/safe_set_text only. Struct props via read/modify/
-- write-back of live sub-structs (never table args). The ONLY table-arg calls
-- are the proven-safe UWidget viewport setters.
-- =====================================================================

local function cls(path)
    local c
    pcall(function() c = StaticFindObject(path) end)
    return obj_valid(c) and c or nil
end

-- Construct a child UWidget directly under the live WidgetTree (the correct
-- runtime recipe). outer = the WidgetTree, NOT the transient package.
local function new_obj(klass, outer)
    if not obj_valid(klass) or not obj_valid(outer) then return nil end
    if type(StaticConstructObject_Internal) ~= "function" then return nil end
    local w = nil
    pcall(function() w = StaticConstructObject_Internal(klass, outer) end)
    if obj_valid(w) then return w end
    return nil
end

-- r/m/w a FLinearColor-typed PROPERTY in place (no FLinearColor ctor).
local function set_linear_prop(owner, propName, r, g, b, a)
    if owner == nil or propName == nil then return end
    pcall(function()
        local c = owner[propName]
        if c == nil then return end
        c.R = r; c.G = g; c.B = b; c.A = a
        owner[propName] = c
    end)
end

-- r/m/w a TextBlock ColorAndOpacity (FSlateColor.SpecifiedColor).
local function set_text_color(t, r, g, b, a)
    if t == nil then return end
    pcall(function()
        local c = t.ColorAndOpacity
        if c == nil then return end
        local sc = c.SpecifiedColor
        if sc == nil then return end
        sc.R = r; sc.G = g; sc.B = b; sc.A = a
        c.SpecifiedColor = sc
        t.ColorAndOpacity = c
    end)
end

local function set_brush_draw_type(border, drawType)
    if border == nil then return false end
    local applied = false
    local ok = pcall(function()
        local brush = border.Background
        if brush == nil then return end
        brush.DrawAs = drawType
        local outline = brush.OutlineSettings
        if outline ~= nil then
            local radii = outline.CornerRadii
            if radii ~= nil then
                radii.X = 8.0; radii.Y = 8.0; radii.Z = 8.0; radii.W = 8.0
                outline.CornerRadii = radii
                brush.OutlineSettings = outline
            end
        end
        border.Background = brush
        applied = true
    end)
    return ok and applied
end

-- Belt-and-suspenders brush tint (Border.BrushColor can no-op on this build).
local function set_brush_tint(border, r, g, b, a)
    if border == nil then return end
    pcall(function()
        local brush = border.Background
        if brush == nil then return end
        local tint = brush.TintColor
        if tint == nil then return end
        local sc = tint.SpecifiedColor
        if sc == nil then return end
        sc.R = r; sc.G = g; sc.B = b; sc.A = a
        tint.SpecifiedColor = sc
        brush.TintColor = tint
        border.Background = brush
    end)
end

local function set_border_padding(border, l, t, r, b)
    if border == nil then return end
    pcall(function()
        local p = border.Padding
        if p == nil then return end
        p.Left = l; p.Top = t; p.Right = r; p.Bottom = b
        border.Padding = p
    end)
end

-- v0.8.1 CRASH-RULE FIX: read/modify/write-back the TARGET widget's OWN
-- FSlateFontInfo and set only the scalar Size. We do NOT assign a foreign
-- FSlateFontInfo captured off another widget — cross-widget struct assignment
-- through reflection is a marshal/AV risk on this build. The srcFont arg is
-- accepted for call-site compatibility but intentionally IGNORED.
local function apply_font(t, _srcFont, sz)
    if t == nil then return end
    pcall(function()
        local f = t.Font
        if f == nil then return end
        f.Size = sz
        t.Font = f
    end)
end

local function set_shadow(t)
    if t == nil then return end
    pcall(function()
        local o = t.ShadowOffset
        if o == nil then return end
        o.X = 1.0; o.Y = 1.0
        t.ShadowOffset = o
    end)
    pcall(function()
        local c = t.ShadowColorAndOpacity
        if c == nil then return end
        c.R = 0; c.G = 0.02; c.B = 0.05; c.A = 0.85
        t.ShadowColorAndOpacity = c
    end)
end

-- v0.8.1: capture_watermark_font() removed. Reading a foreign live
-- FSlateFontInfo off another widget only existed to reassign it cross-widget,
-- which violated the crash rule; apply_font now sizes each widget's own font.

-- Drop stale panel pointers so a rebuild starts clean (does NOT touch the
-- backoff counters — those gate whether we even try again).
--
-- v0.8.1: a root that was AddToViewport'd MUST be detached before its Lua ref
-- is dropped, or the dead widget stays parented to the viewport (leak) and a
-- later rebuild stacks a SECOND root on top. Collapse + RemoveFromParent the
-- existing root first; every call is pcall'd and obj_valid-guarded.
local function clear_panel_refs()
    local root = render.PanelRoot
    if obj_valid(root) then
        pcall(function() root:SetVisibility(VIS_COLLAPSED) end)
        pcall(function() root:RemoveFromParent() end)
    end
    render.PanelRoot    = nil
    render.PanelTree    = nil
    render.PanelAdded   = false
    render.Border       = nil
    render.Header       = nil
    render.Scroll       = nil
    render.Feed         = nil
    render.FeedRows     = {}
    render.Input        = nil
    render.PanelVisible = -1
    render.headerSet    = false
    render.lastFeedSig  = nil
    render.lastInput    = nil
end

-- panel_valid (assigned to the forward-declared upvalue from apply_visibility).
local function feed_rows_valid()
    if type(render.FeedRows) ~= "table" or #render.FeedRows ~= MAX_FEED_ROWS then return false end
    for _, row in ipairs(render.FeedRows) do
        if not obj_valid(row) then return false end
    end
    return true
end

-- v0.8.1: panel_valid() REQUIRES render.PanelAdded (a VERIFIED viewport
-- attach). A built-but-unattached or dead panel therefore reports invalid, so
-- it can NEVER suppress the proven watermark via apply_visibility().
panel_valid = function()
    return render.PanelAdded == true
        and obj_valid(render.PanelRoot)
        and obj_valid(render.Feed)
        and obj_valid(render.Input)
        and feed_rows_valid()
end

-- v0.8.1: Build the navy-glass panel TRY-ONCE. Returns true on success.
-- Reachable ONLY when opted in (panelDisabled defaults true unless the marker
-- enabled it). Any failure sets panelDisabled=true forever, so this function
-- runs its full body at most ONCE per session — there is no retry/cooldown loop
-- and therefore no per-tick build spam / game-thread starvation. Guarded by
-- render.building (cross-thread re-entry). MUST run on the game thread.
local function build_rich_panel()
    if panel_valid() then return true end
    if render.panelDisabled then return false end   -- opted out, or already failed once
    if render.building then return false end
    if render.panelTried then return false end      -- belt-and-suspenders: never attempt twice

    render.building = true
    render.panelTried = true

    local built = false
    local ok = pcall(function()
        -- 1. Resolve engine UMG classes.
        local UserWidgetC = cls("/Script/UMG.UserWidget")
        local BorderC     = cls("/Script/UMG.Border")
        local VBoxC       = cls("/Script/UMG.VerticalBox")
        local ScrollC     = cls("/Script/UMG.ScrollBox")   -- optional (last-N fallback if nil)
        local TextC       = cls("/Script/UMG.TextBlock")
        -- CanvasPanel intentionally not required: the Border is the root widget.
        if not (UserWidgetC and BorderC and VBoxC and TextC) then
            log_line("panel: UMG class resolve failed")
            return
        end

        -- 2. Root UserWidget via WidgetBlueprintLibrary::Create with a REAL
        --    owning PlayerController as both WorldContext and owning player.
        local wbl = get_widget_blueprint_library()
        if not obj_valid(wbl) then
            log_line("panel: WidgetBlueprintLibrary CDO missing")
            return
        end
        local pc = GetPC()
        if not obj_valid(pc) then
            log_line("panel: no PlayerController yet")
            return
        end
        local root = nil
        pcall(function() root = wbl:Create(pc, UserWidgetC, pc) end)
        if not obj_valid(root) then
            log_line("panel: root UserWidget Create failed")
            return
        end

        local tree = nil
        pcall(function() tree = root.WidgetTree end)
        if not obj_valid(tree) then
            log_line("panel: root WidgetTree missing")
            return
        end

        -- 3. Construct children directly under the WidgetTree (the fix).
        --    v0.8.1: the Border is the WidgetTree RootWidget DIRECTLY (no
        --    CanvasPanel). A CanvasPanel slot needs an explicit size/offset or
        --    its child renders zero-size (a blank panel that still reports
        --    obj_valid); a Border auto-sizes to its content, so making it the
        --    root sidesteps the unsized-canvas-slot problem with one fewer
        --    construct + parent call.
        local border = new_obj(BorderC, tree)
        if not obj_valid(border) then log_line("panel: Border construct failed"); return end
        pcall(function() tree.RootWidget = border end)

        local vbox = new_obj(VBoxC, tree)
        if not obj_valid(vbox) then log_line("panel: VerticalBox construct failed"); return end

        local header = new_obj(TextC, tree)
        if not obj_valid(header) then log_line("panel: header construct failed"); return end

        -- ScrollBox is preferred; if it fails to construct, fall back to the
        -- feed VBox directly inside the outer VBox (last-N window, no scroll).
        local scroll = ScrollC and new_obj(ScrollC, tree) or nil
        if ScrollC and not obj_valid(scroll) then
            log_line("panel: ScrollBox construct failed; using fixed last-N window")
            scroll = nil
        end

        local feed = new_obj(VBoxC, tree)
        if not obj_valid(feed) then log_line("panel: feed VerticalBox construct failed"); return end

        local input = new_obj(TextC, tree)
        if not obj_valid(input) then log_line("panel: input construct failed"); return end

        -- 4. Parent via reflected BlueprintCallable UObject-ptr APIs (the
        --    returned PanelSlots are ignored; Border is already the root).
        pcall(function() border:AddChild(vbox) end)
        pcall(function() vbox:AddChildToVerticalBox(header) end)
        if obj_valid(scroll) then
            pcall(function() vbox:AddChildToVerticalBox(scroll) end)
            pcall(function() scroll:AddChild(feed) end)
        else
            pcall(function() vbox:AddChildToVerticalBox(feed) end)
        end
        pcall(function() vbox:AddChildToVerticalBox(input) end)

        -- 5. v0.8.1: fonts are sized via each widget's OWN FSlateFontInfo
        --    (apply_font reads/modifies/writes-back the target's struct). We do
        --    NOT capture + reassign a foreign font from another widget — that
        --    crossed the crash rule (cross-widget struct marshal). No capture.

        -- 6. Border = navy translucent glass.
        if not set_brush_draw_type(border, BRUSH_DRAW_ROUNDED_BOX) then
            set_brush_draw_type(border, BRUSH_DRAW_BOX)
        end
        set_linear_prop(border, "BrushColor", 0.04, 0.07, 0.12, 0.55)
        set_brush_tint(border, 0.04, 0.07, 0.12, 0.55)
        set_border_padding(border, 12.0, 8.0, 12.0, 8.0)

        -- 7. Header: "BEACON CHAT", soft cyan.
        safe_set_text(header, "BEACON CHAT")
        set_text_color(header, 0.55, 0.85, 0.95, 1.0)
        apply_font(header, nil, 12)
        set_shadow(header)

        -- 8. Feed rows: a fixed pool of single TextBlock rows inside the feed
        --    VBox. Each row is ONE TextBlock holding "sender: body" (white).
        --    One widget per row keeps the construct surface small + reliable;
        --    last-N windowing writes the most-recent N and collapses the rest.
        local rowWidgets = {}
        for i = 1, MAX_FEED_ROWS do
            local row = new_obj(TextC, tree)
            if not obj_valid(row) then
                log_line("panel: feed row construct failed at " .. tostring(i))
                return
            end
            pcall(function() feed:AddChildToVerticalBox(row) end)
            safe_set_text(row, "")
            set_text_color(row, 0.90, 0.94, 0.98, 1.0)
            apply_font(row, nil, 13)
            set_shadow(row)
            pcall(function() row.AutoWrapText = true end)
            pcall(function() row.WrapTextAt = FEED_BODY_WRAP_AT end)
            pcall(function() row:SetVisibility(VIS_COLLAPSED) end)
            rowWidgets[i] = row
        end

        -- 9. Input line: dim gray-blue.
        set_text_color(input, 0.55, 0.62, 0.70, 0.80)
        apply_font(input, nil, 12)
        set_shadow(input)

        -- 10. Position via the proven-safe viewport table setters (top-left).
        pcall(function() root:SetAlignmentInViewport({ X = 0.0, Y = 0.0 }) end)
        pcall(function() root:SetAnchorsInViewport({ Minimum = { X = 0, Y = 0 }, Maximum = { X = 0, Y = 0 } }) end)
        pcall(function() root:SetPositionInViewport({ X = PANEL_X, Y = PANEL_Y }, false) end)
        pcall(function() root:SetDesiredSizeInViewport({ X = PANEL_W, Y = PANEL_H }) end)

        -- 11. Start hidden; the selector flips it HitTestInvisible when shown.
        --     Publish render.PanelRoot BEFORE AddToViewport so that if the next
        --     step (or any later step) fails, clear_panel_refs() can find +
        --     RemoveFromParent() this root instead of leaking it parented.
        render.PanelRoot  = root
        render.PanelTree  = tree
        render.PanelAdded = false
        pcall(function() root:SetVisibility(VIS_COLLAPSED) end)
        pcall(function() root:SynchronizeProperties() end)
        pcall(function() root:AddToViewport(Z_ORDER) end)

        -- 11a. VERIFY the attach actually happened. AddToViewport can pcall-
        --      succeed yet not truly parent the widget; if so we must NOT treat
        --      the panel as live (an unattached-but-obj_valid panel would
        --      otherwise suppress the watermark with no fallback). PanelAdded is
        --      the gate panel_valid() requires; set it ONLY on a positive
        --      IsInViewport read.
        local inVp = false
        pcall(function() inVp = (root:IsInViewport() == true) end)
        render.PanelAdded = inVp
        if not inVp then
            log_line("panel: AddToViewport did not attach (IsInViewport=false) — treating as build failure")
            return   -- leaves built=false -> failure tail detaches + disables
        end

        render.Border       = border
        render.Header       = header
        render.Scroll       = scroll
        render.Feed         = feed
        render.FeedRows     = rowWidgets
        render.Input        = input
        render.PanelVisible = VIS_COLLAPSED
        render.headerSet    = true
        render.lastFeedSig  = nil
        render.lastInput    = nil
        built = panel_valid()   -- now also requires render.PanelAdded == true
        if built then
            activity("rich chat panel ready (press Enter to chat)")
            log_line("panel: navy-glass chat panel built + attached (scroll="
                .. (obj_valid(scroll) and "ScrollBox" or "fixed-N") .. ")")
        end
    end)

    render.building = false

    if not (ok and built) then
        -- v0.8.1 TRY-ONCE: ANY failed/unverified build disables the rich panel
        -- FOREVER and hands the screen to the proven watermark — no cooldown
        -- retry, no per-tick respam, no game-thread starvation. clear_panel_refs
        -- also RemoveFromParent()s a half-attached root so nothing leaks.
        render.panelAttempts = render.panelAttempts + 1
        render.panelFailedAt = os.clock()
        clear_panel_refs()
        render.panelDisabled = true
        log_line(string.format(
            "panel: build failed (pcall_ok=%s) — disabling rich panel for this session; watermark fallback owns the screen",
            tostring(ok)))
        return false
    end

    return true
end

-- Row data assembly for the feed (last-N window of messages + echoes).
local function one_line(s)
    s = tostring(s or "")
    return s:gsub("[\r\n\t]", " "):gsub("^%s+", ""):gsub("%s+$", "")
end

local function short_sender(s)
    s = one_line(s)
    if #s > 24 then return s:sub(1, 21) .. "..." end
    return s
end

local function build_feed_rows()
    local rows = {}
    local function push(sender, body)
        body = one_line(body)
        if body == "" then return end
        rows[#rows + 1] = { sender = short_sender(sender), body = body }
    end
    if motd_state.text ~= "" and (os.time() - motd_state.shown_at) < MOTD_BANNER_SECONDS then
        push("BEACON", motd_state.text)
    end
    prune_echoes()
    if #feed_state.messages == 0 and #pending_echoes == 0 then
        push("", "No messages yet")
    else
        for _, m in ipairs(feed_state.messages) do push(m.sender, m.body) end
        for _, e in ipairs(pending_echoes) do push(e.sender, e.body) end
    end
    -- Keep only the most recent MAX_FEED_ROWS (last-N window self-scrolls).
    if #rows > MAX_FEED_ROWS then
        local out = {}
        for i = #rows - MAX_FEED_ROWS + 1, #rows do out[#out + 1] = rows[i] end
        rows = out
    end
    return rows
end

local function feed_rows_signature(rows)
    local parts = {}
    for i, row in ipairs(rows) do
        parts[i] = (row.sender or "") .. "\31" .. (row.body or "")
    end
    return table.concat(parts, "\30")
end

local function apply_feed_rows(rows)
    if type(render.FeedRows) ~= "table" then return end
    for i = 1, MAX_FEED_ROWS do
        local w = render.FeedRows[i]
        if obj_valid(w) then
            local row = rows[i]
            if row ~= nil then
                local line = (row.sender ~= "" and (row.sender .. ": ") or "") .. (row.body or "")
                safe_set_text(w, line)
                pcall(function() w:SetVisibility(VIS_HIT_TEST_INVISIBLE) end)
            else
                safe_set_text(w, "")
                pcall(function() w:SetVisibility(VIS_COLLAPSED) end)
            end
        end
    end
end

local function build_input_text()
    if typing.active then return build_typing_line() end
    return "Press Enter to chat"
end

-- Rich-panel render: refresh feed rows + input line, then scroll to newest.
-- Does NOT call apply_visibility (the selector owns that).
local function render_panel_tick()
    if not panel_valid() then return false end

    -- Header once.
    if not render.headerSet then
        safe_set_text(render.Header, "BEACON CHAT")
        render.headerSet = true
    end

    -- Feed rows when the transcript changed (forced repaint) OR on the FIRST
    -- paint after a fresh build (lastFeedSig == nil). The first-paint case is
    -- the correctness fix for a DELAYED build success: build sets lastFeedSig
    -- nil, but render.dirty may already have been consumed by the watermark on
    -- the failing attempt, so a dirty-only gate would leave the new panel blank
    -- until the next message. Painting on lastFeedSig==nil closes that hole.
    if render.dirty or render.lastFeedSig == nil then
        local rows = build_feed_rows()
        local sig = feed_rows_signature(rows)
        if sig ~= render.lastFeedSig then
            render.lastFeedSig = sig
            apply_feed_rows(rows)
            -- Auto-scroll to the newest line on the next layout pass.
            if obj_valid(render.Scroll) then
                pcall(function() render.Scroll:ScrollToEnd() end)
            end
        end
    end

    -- Input line every tick while typing (cursor moves), else on dirty OR on
    -- the first paint after a fresh build (lastInput == nil).
    if typing.active or render.dirty or render.lastInput == nil then
        local it = build_input_text()
        if it ~= render.lastInput then
            render.lastInput = it
            safe_set_text(render.Input, it)
        end
    end

    return true
end

-- =====================================================================
-- Render selector (assigned to the forward-declared render_tick upvalue):
-- prefer the rich panel; fall back to the one-line watermark. Exactly one
-- surface is shown. Runs on the game thread.
-- =====================================================================
render_tick = function()
    -- Warmup: don't fight the HUD before it exists (Nexus waits ~5s).
    if (os.clock() - render.bootTime) < 5.0 then return end
    -- Only meaningful in-server; at the menu there's no pawn / no HUD.
    if not IsInGame() then
        if render.Reparented and not obj_valid(render.Watermark) then
            render.Reparented = false
        end
        -- v0.8.1: a built panel that lost validity (map travel destroyed it)
        -- is TERMINAL — detach it AND disable the panel path for the session so
        -- it never silently rebuilds/respawns. Watermark owns the screen.
        if obj_valid(render.PanelRoot) and not panel_valid() then
            clear_panel_refs()
            render.panelDisabled = true
            log_line("panel: invalidated (map travel) — disabled for session; watermark fallback owns the screen")
        end
        return
    end

    -- v0.8.1: detect a post-success invalidation IN-WORLD too (root went stale
    -- without leaving the world). Same terminal rule: detach + disable + fall
    -- through to the watermark; never rebuild a panel that already died once.
    if not render.panelDisabled and obj_valid(render.PanelRoot) and not panel_valid() then
        clear_panel_refs()
        render.panelDisabled = true
        log_line("panel: live panel invalidated — disabled for session; watermark fallback owns the screen")
    end

    -- Try the rich panel (only if opted in + not disabled), then render it.
    local panelLive = false
    if not render.panelDisabled then
        build_rich_panel()
        if panel_valid() then
            panelLive = render_panel_tick()
        end
    end

    -- If the panel isn't live, fall back to the one-line watermark (the proven,
    -- always-available surface). Track whether ANY surface actually painted.
    local watermarkLive = false
    if not panelLive then
        watermarkLive = render_watermark_tick()
    end

    -- Only clear the forced-repaint flag once a surface actually consumed it.
    -- If neither surface was live this tick (e.g. the watermark hasn't found
    -- WBP_Watermark yet), KEEP dirty set so the next tick still repaints —
    -- otherwise a delayed surface-go-live would render blank until the next
    -- message re-set dirty. (correctness fixes 2 + 3)
    if panelLive or watermarkLive then
        render.dirty = false
    end

    apply_visibility()
end

-- v0.8.3 ROOT-CAUSE FIX FOR DEAD WASD/MOUSE-LOOK AFTER TYPING.
--
-- Up through v0.8.2 this function called UWidgetBlueprintLibrary
-- SetInputMode_GameOnly(pc) + SetFocusToGameViewport() + ClearKeyboardFocus/
-- ClearUserFocus on every typing-end. Runtime evidence on the live SN2 server
-- (UE4SS / UE5.6 / D3D12) proved that the PlayerController + pawn were in a
-- PERFECTLY NORMAL state after typing (ignoreMove=false ignoreLook=false
-- cursor=false walk/swim/jump restored) and yet WASD + mouse-look were
-- completely dead. The blocker was NOT the movement lock — it was the
-- viewport/Slate input ROUTING, and those very "reclaim" calls are what
-- detached keyboard/mouse-axis routing from the live FSceneViewport so raw
-- input never reached PlayerInput again.
--
-- The PROVEN Nexus "MultiplayerChat" mod NEVER touches input mode or viewport
-- focus at all — it only freezes/unfreezes movement — and movement works fine
-- in it. Beacon already matches Nexus everywhere else: open_typing() grabs NO
-- keyboard focus (global RegisterKeyBind hooks capture keys without it) and the
-- overlay renders HitTestInvisible, so nothing ever captures input that would
-- NEED reclaiming. There is therefore nothing to reclaim, and any reclaim call
-- only does harm on this build.
--
-- So this is now a MOVEMENT-ONLY release: UnfreezeMovement() (called by the
-- callers right before this) already restored ignore flags, speeds, jump, and
-- FlushPressedKeys. Here we ONLY assert the cursor is hidden (a plain bool
-- write, no Slate routing) and log a full input/movement diagnostic so the next
-- test can confirm the viewport/PC state before-vs-after typing. NO SetInputMode,
-- NO SetFocusToGameViewport, NO ClearKeyboardFocus/ClearUserFocus.
local function release_ui_capture(reason)
    local pc = GetPC()
    if obj_valid(pc) then
        pcall(function() pc.bShowMouseCursor = false end)   -- plain bool, no Slate routing
    end
    -- Diagnostic: full input/movement snapshot AFTER typing ends. Pair this with
    -- the "movement freeze before" line to confirm before-vs-after-typing state.
    local pawn, pawnSource = GetMovementPawn(pc)
    log_movement_state("input release " .. tostring(reason or "typing"), pc, pawn, pawnSource)
end

local function repaint_input()
    typing.lastKeyAt = os.clock()   -- every typing keystroke lands here (failsafe clock)
    mark_activity()
    run_on_game_thread(function()
        local ok, err = pcall(render_tick)
        if not ok then log_line("input render error: " .. tostring(err)) end
    end)
end

local function open_typing()
    if typing.active then return end
    if not IsInGame() then return end          -- only in-server
    if chat_off_marker() then return end       -- chat toggled off
    typing.active  = true
    typing.buffer  = ""
    typing.cursor  = 0
    typing.sentIdx = 0
    typing.lastKeyAt = os.clock()
    FreezeMovement()
    mark_activity()
    run_on_game_thread(function()
        render_tick()   -- v0.7.1: do NOT grab keyboard focus; global keybinds capture keys
    end)
end

local function close_typing()
    -- Reverse EVERYTHING freeze did. This is the stuck-movement fix.
    typing.active  = false
    typing.buffer  = ""
    typing.cursor  = 0
    typing.sentIdx = 0
    UnfreezeMovement()
    mark_activity()
    run_on_game_thread(function()
        render_tick()
        release_ui_capture("close")
    end)
end

local function insert_char(ch)
    if not typing.active then return end
    if #typing.buffer >= CHAT_INPUT_CAP then return end
    typing.buffer = string.sub(typing.buffer, 1, typing.cursor) .. ch
                  .. string.sub(typing.buffer, typing.cursor + 1)
    typing.cursor = typing.cursor + 1
    typing.sentIdx = 0
    repaint_input()
end

local function prev_word_boundary(str, pos)
    if pos <= 0 then return 0 end
    local p = pos
    while p > 0 and string.sub(str, p, p) == " " do p = p - 1 end
    while p > 0 and string.sub(str, p, p) ~= " " do p = p - 1 end
    return p
end

local function next_word_boundary(str, pos)
    local len = #str
    if pos >= len then return len end
    local p = pos + 1
    while p <= len and string.sub(str, p, p) ~= " " do p = p + 1 end
    while p <= len and string.sub(str, p, p) == " " do p = p + 1 end
    return p - 1
end

-- ENTER: send via Beacon's HTTP outbox + local echo. NO ServerExecRPC.
local function send_typing()
    if not typing.active then return end
    local body = typing.buffer
    -- Close first so movement is restored even if the send path is slow.
    typing.active  = false
    typing.cursor  = 0
    typing.sentIdx = 0
    typing.buffer  = ""
    UnfreezeMovement()
    run_on_game_thread(function()
        render_tick()
        release_ui_capture("send")
    end)
    if body and body:gsub("%s", "") ~= "" then
        typing.sent[#typing.sent + 1] = body
        if #typing.sent > 50 then table.remove(typing.sent, 1) end
        queue_outbox(body)   -- HTTP send + add_echo() (instant local echo)
    end
    repaint_input()
end

-- ESC: cancel typing, restore movement, and block the pause menu.
local function cancel_typing()
    if not typing.active then return end
    close_typing()
    run_on_game_thread(function()
        local pc = GetPC()
        if pc then pcall(function() pc:SetPause(false) end) end   -- block the pause menu
    end)
end

-- ENTER opens chat by default (a configured A-Z letter opener can ALSO open it,
-- see OPEN_KEY_LETTER). Returns true only when we actually open, so the opener
-- key still works in menus / passes through to the game otherwise.
local function try_open_chat()
    if typing.active then return false end
    if not IsInGame() then return false end           -- in-world only
    if chat_off_marker() then return false end         -- chat toggled off
    local pc = GetPC()
    local cursorUp = false
    if pc then pcall(function() cursorUp = pc.bShowMouseCursor end) end
    if cursorUp then return false end                  -- a menu is open; leave ENTER alone
    open_typing()
    return true
end

-- =====================================================================
-- Keybind registration (ADOPT from Nexus). RegisterKeyBind modifier lists
-- are plain integer arrays — a UE4SS-native table param, NOT a UScriptStruct
-- marshal — so they're crash-rule safe.
-- =====================================================================
local function has_key(name) return Key ~= nil and Key[name] ~= nil end
local function resolve_key(...)
    for _, name in ipairs({ ... }) do
        if has_key(name) then return Key[name] end
    end
    return nil
end

-- Letters: lowercase plain, uppercase on Shift. When NOT typing a letter only
-- opens chat if it is the configured OPEN_KEY_LETTER (default opener is ENTER,
-- so by default no letter opens — letters pass straight through to the game).
local Letters = {
    "A","B","C","D","E","F","G","H","I","J","K","L","M",
    "N","O","P","Q","R","S","T","U","V","W","X","Y","Z"
}
if not DIAG_NOKEYS and Key ~= nil and type(RegisterKeyBind) == "function" then  -- bisection: skip per-key capture binds
    for _, letter in ipairs(Letters) do
        if Key[letter] then
            pcall(function()
                RegisterKeyBind(Key[letter], function()
                    -- While typing: the letter inserts (consumed).
                    if typing.active then
                        insert_char(string.lower(letter)); return true
                    end
                    -- v0.8.3: if THIS letter is the configured chat-open key
                    -- (default opener is ENTER, so normally no letter opens),
                    -- open chat and CONSUME only when it actually opens — so the
                    -- key can never also fire the game action. Otherwise pass
                    -- straight through to the game (no consume).
                    if OPEN_KEY_LETTER == letter then
                        if try_open_chat() then return true end
                    end
                end)
            end)
            -- Uppercase on Shift.
            if ModifierKey and ModifierKey.SHIFT then
                pcall(function()
                    RegisterKeyBind(Key[letter], { ModifierKey.SHIFT }, function()
                        -- return true so the shifted letter is consumed while
                        -- typing instead of also firing the game key.
                        if typing.active then insert_char(letter); return true end
                    end)
                end)
            end
        end
    end
end

-- Numbers + shifted symbols + space.
local NumberKeys = {
    ZERO="0", ONE="1", TWO="2", THREE="3", FOUR="4", FIVE="5", SIX="6", SEVEN="7", EIGHT="8", NINE="9",
    NUM_ZERO="0", NUM_ONE="1", NUM_TWO="2", NUM_THREE="3", NUM_FOUR="4",
    NUM_FIVE="5", NUM_SIX="6", NUM_SEVEN="7", NUM_EIGHT="8", NUM_NINE="9",
}
local ShiftedNumberChars = {
    ZERO=")", ONE="!", TWO="@", THREE="#", FOUR="$", FIVE="%", SIX="^", SEVEN="&", EIGHT="*", NINE="(",
    NUM_ZERO=")", NUM_ONE="!", NUM_TWO="@", NUM_THREE="#", NUM_FOUR="$",
    NUM_FIVE="%", NUM_SIX="^", NUM_SEVEN="&", NUM_EIGHT="*", NUM_NINE="(",
}
if not DIAG_NOKEYS and Key ~= nil and type(RegisterKeyBind) == "function" then  -- bisection: skip per-key capture binds
    for keyName, char in pairs(NumberKeys) do
        if Key[keyName] then
            pcall(function()
                RegisterKeyBind(Key[keyName], function()
                    if typing.active then insert_char(char); return true end
                end)
            end)
            if ModifierKey and ModifierKey.SHIFT then
                pcall(function()
                    RegisterKeyBind(Key[keyName], { ModifierKey.SHIFT }, function()
                        if typing.active then insert_char(ShiftedNumberChars[keyName] or char); return true end
                    end)
                end)
            end
        end
    end

    -- Common punctuation while typing (best-effort — only binds keys this
    -- build exposes). Plain + shifted forms.
    local PunctKeys = {
        PERIOD     = { ".", ">" },
        COMMA      = { ",", "<" },
        SLASH      = { "/", "?" },
        SEMICOLON  = { ";", ":" },
        APOSTROPHE = { "'", '"' },
        LEFT_BRACKET  = { "[", "{" },
        RIGHT_BRACKET = { "]", "}" },
        BACKSLASH  = { "\\", "|" },
        HYPHEN     = { "-", "_" },
        EQUALS     = { "=", "+" },
        TILDE      = { "`", "~" },
        ADD        = { "+", "+" },
        SUBTRACT   = { "-", "-" },
        MULTIPLY   = { "*", "*" },
        DIVIDE     = { "/", "/" },
        DECIMAL    = { ".", "." },
    }
    for keyName, pair in pairs(PunctKeys) do
        if Key[keyName] then
            local plain, shifted = pair[1], pair[2]
            pcall(function()
                RegisterKeyBind(Key[keyName], function()
                    if typing.active then insert_char(plain); return true end
                end)
            end)
            if ModifierKey and ModifierKey.SHIFT then
                pcall(function()
                    RegisterKeyBind(Key[keyName], { ModifierKey.SHIFT }, function()
                        if typing.active then insert_char(shifted); return true end
                    end)
                end)
            end
        end
    end

    -- Space.
    local kSpace = resolve_key("SPACE", "SpaceBar", "SPACE_BAR", "SPACEBAR")
    if kSpace then
        pcall(function()
            RegisterKeyBind(kSpace, function()
                if typing.active then insert_char(" "); return true end
            end)
        end)
        if ModifierKey and ModifierKey.SHIFT then
            pcall(function()
                RegisterKeyBind(kSpace, { ModifierKey.SHIFT }, function()
                    if typing.active then insert_char(" "); return true end
                end)
            end)
        end
    end

    -- Backspace (+ Ctrl = delete previous word).
    local kBack = resolve_key("BACKSPACE", "BackSpace", "BACK_SPACE", "Backspace")
    if kBack then
        pcall(function()
            RegisterKeyBind(kBack, function()
                if not typing.active or typing.cursor <= 0 then return end
                typing.buffer = string.sub(typing.buffer, 1, typing.cursor - 1)
                              .. string.sub(typing.buffer, typing.cursor + 1)
                typing.cursor = typing.cursor - 1
                typing.sentIdx = 0
                repaint_input()
            end)
        end)
        if ModifierKey and ModifierKey.CONTROL then
            pcall(function()
                RegisterKeyBind(kBack, { ModifierKey.CONTROL }, function()
                    if not typing.active or typing.cursor <= 0 then return end
                    local nc = prev_word_boundary(typing.buffer, typing.cursor)
                    typing.buffer = string.sub(typing.buffer, 1, nc)
                                  .. string.sub(typing.buffer, typing.cursor + 1)
                    typing.cursor = nc
                    typing.sentIdx = 0
                    repaint_input()
                end)
            end)
        end
        -- Shift+Backspace: a plain bind doesn't fire while Shift is held, so
        -- backspacing right after a capital went dead. Mirror the plain action.
        if ModifierKey and ModifierKey.SHIFT then
            pcall(function()
                RegisterKeyBind(kBack, { ModifierKey.SHIFT }, function()
                    if not typing.active or typing.cursor <= 0 then return end
                    typing.buffer = string.sub(typing.buffer, 1, typing.cursor - 1)
                                  .. string.sub(typing.buffer, typing.cursor + 1)
                    typing.cursor = typing.cursor - 1
                    typing.sentIdx = 0
                    repaint_input()
                    return true
                end)
            end)
        end
    end

    -- Arrows: Left/Right cursor (+ Ctrl word jump), Up/Down sent-history recall.
    local kLeft  = resolve_key("LEFT_ARROW", "Left", "LEFT", "LeftArrow")
    local kRight = resolve_key("RIGHT_ARROW", "Right", "RIGHT", "RightArrow")
    local kUp    = resolve_key("UP_ARROW", "Up", "UP", "PAGE_UP")
    local kDown  = resolve_key("DOWN_ARROW", "Down", "DOWN", "PAGE_DOWN")

    if kLeft then
        pcall(function()
            RegisterKeyBind(kLeft, function()
                if not typing.active then return end
                if typing.cursor > 0 then typing.cursor = typing.cursor - 1; repaint_input() end
            end)
        end)
        if ModifierKey and ModifierKey.CONTROL then
            pcall(function()
                RegisterKeyBind(kLeft, { ModifierKey.CONTROL }, function()
                    if not typing.active then return end
                    typing.cursor = prev_word_boundary(typing.buffer, typing.cursor)
                    repaint_input()
                end)
            end)
        end
    end
    if kRight then
        pcall(function()
            RegisterKeyBind(kRight, function()
                if not typing.active then return end
                if typing.cursor < #typing.buffer then typing.cursor = typing.cursor + 1; repaint_input() end
            end)
        end)
        if ModifierKey and ModifierKey.CONTROL then
            pcall(function()
                RegisterKeyBind(kRight, { ModifierKey.CONTROL }, function()
                    if not typing.active then return end
                    typing.cursor = next_word_boundary(typing.buffer, typing.cursor)
                    repaint_input()
                end)
            end)
        end
    end
    if kUp then
        pcall(function()
            RegisterKeyBind(kUp, function()
                if not typing.active then return end
                if #typing.sent > 0 and typing.sentIdx < #typing.sent then
                    typing.sentIdx = typing.sentIdx + 1
                    typing.buffer = typing.sent[#typing.sent - typing.sentIdx + 1]
                    typing.cursor = #typing.buffer
                    repaint_input()
                end
            end)
        end)
    end
    if kDown then
        pcall(function()
            RegisterKeyBind(kDown, function()
                if not typing.active then return end
                if typing.sentIdx > 1 then
                    typing.sentIdx = typing.sentIdx - 1
                    typing.buffer = typing.sent[#typing.sent - typing.sentIdx + 1]
                    typing.cursor = #typing.buffer
                elseif typing.sentIdx == 1 then
                    typing.sentIdx = 0
                    typing.buffer = ""
                    typing.cursor = 0
                end
                repaint_input()
            end)
        end)
    end

    -- Shift variants for the arrows (same reason as Shift+Backspace: a plain
    -- bind is dead while Shift is held). Mirror the plain cursor/history moves.
    if ModifierKey and ModifierKey.SHIFT then
        if kLeft then pcall(function() RegisterKeyBind(kLeft, { ModifierKey.SHIFT }, function()
            if not typing.active then return end
            if typing.cursor > 0 then typing.cursor = typing.cursor - 1; repaint_input() end
            return true
        end) end) end
        if kRight then pcall(function() RegisterKeyBind(kRight, { ModifierKey.SHIFT }, function()
            if not typing.active then return end
            if typing.cursor < #typing.buffer then typing.cursor = typing.cursor + 1; repaint_input() end
            return true
        end) end) end
        if kUp then pcall(function() RegisterKeyBind(kUp, { ModifierKey.SHIFT }, function()
            if not typing.active then return end
            if #typing.sent > 0 and typing.sentIdx < #typing.sent then
                typing.sentIdx = typing.sentIdx + 1
                typing.buffer = typing.sent[#typing.sent - typing.sentIdx + 1]
                typing.cursor = #typing.buffer
                repaint_input()
            end
            return true
        end) end) end
        if kDown then pcall(function() RegisterKeyBind(kDown, { ModifierKey.SHIFT }, function()
            if not typing.active then return end
            if typing.sentIdx > 1 then
                typing.sentIdx = typing.sentIdx - 1
                typing.buffer = typing.sent[#typing.sent - typing.sentIdx + 1]
                typing.cursor = #typing.buffer
            elseif typing.sentIdx == 1 then
                typing.sentIdx = 0; typing.buffer = ""; typing.cursor = 0
            end
            repaint_input()
            return true
        end) end) end
    end

    -- Enter sends; Esc cancels (and blocks the pause menu).
    --
    -- A bind with no modifier list does NOT fire while Shift/Ctrl is held —
    -- that's exactly why Space/letters/numbers register explicit SHIFT
    -- variants above. Enter had none, so finishing a message with "!" or a
    -- capital and hitting Enter a beat early (Shift still down) sent NOTHING:
    -- the box stayed open and movement stayed frozen until a clean Esc. The
    -- modifier variants below send while typing (Ctrl+Enter is also common
    -- chat muscle-memory) and pass straight through when not typing.
    local function bind_send_variant(k, mod)
        pcall(function()
            RegisterKeyBind(k, { mod }, function()
                if typing.active then send_typing(); return true end
            end)
        end)
    end
    local kEnter = resolve_key("ENTER", "Enter", "RETURN", "Return")
    if kEnter then
        pcall(function()
            RegisterKeyBind(kEnter, function()
                if typing.active then send_typing(); return true end
                if try_open_chat() then return true end   -- ENTER opens chat in-world
            end)
        end)
        if ModifierKey and ModifierKey.SHIFT then bind_send_variant(kEnter, ModifierKey.SHIFT) end
        if ModifierKey and ModifierKey.CONTROL then bind_send_variant(kEnter, ModifierKey.CONTROL) end
    end
    local kEnterNum = resolve_key("NUM_ENTER", "NumPadEnter", "ENTER_NUM")
    if kEnterNum then
        pcall(function()
            RegisterKeyBind(kEnterNum, function()
                if typing.active then send_typing(); return true end
                if try_open_chat() then return true end
            end)
        end)
        if ModifierKey and ModifierKey.SHIFT then bind_send_variant(kEnterNum, ModifierKey.SHIFT) end
        if ModifierKey and ModifierKey.CONTROL then bind_send_variant(kEnterNum, ModifierKey.CONTROL) end
    end
    local kEsc = resolve_key("ESCAPE", "Escape", "ESC", "Esc")
    if kEsc then
        pcall(function()
            RegisterKeyBind(kEsc, function()
                -- v0.8.2 MUST-FIX: consume Esc while typing so the native pause
                -- menu never also receives it (SetPause(false) is best-effort +
                -- races). Pass Esc straight through when not typing.
                if typing.active then cancel_typing(); return true end
            end)
        end)
        -- Same modifier gap as Enter: Shift+Esc while typing must still cancel.
        if ModifierKey and ModifierKey.SHIFT then
            pcall(function()
                RegisterKeyBind(kEsc, { ModifierKey.SHIFT }, function()
                    if typing.active then cancel_typing(); return true end
                end)
            end)
        end
    end

    -- F7: toggle the CHAT_OFF marker (debug-menu safe fallback). Flips the
    -- file Beacon's visibility gate already reads, plus an on-screen confirm.
    local kF7 = resolve_key("F7")
    if kF7 then
        pcall(function()
            RegisterKeyBind(kF7, function()
                local nowOff = chat_off_marker()
                if nowOff then
                    pcall(function() os.remove(CHAT_OFF_FILE) end)     -- chat ON
                else
                    pcall(function()
                        local f = io.open(CHAT_OFF_FILE, "wb")
                        if f then f:write("1"); f:close() end
                    end)
                end
                local turnedOff = not nowOff
                chat_off_cached = turnedOff
                -- one-shot on-screen confirmation: pulse the overlay + a line.
                motd_state.text = turnedOff
                    and "Beacon chat: OFF (F7 to re-enable)"
                    or  "Beacon chat: ON"
                motd_state.shown_at = os.time()
                mark_activity()
                log_line("F7 toggle -> chat " .. (turnedOff and "OFF" or "ON"))
                -- When turning OFF, force-hide immediately on the next tick.
                run_on_game_thread(function() render_tick() end)
            end)
        end)
        log_line("F7 chat-toggle keybind registered")
    end
end

-- =====================================================================
-- Poll loop (KEEP Beacon's file pipeline) + render tick + fadeout.
-- =====================================================================
if type(LoopAsync) == "function" then
    LoopAsync(POLL_INTERVAL_MS, function()
        local ok, err = pcall(function()
            refresh_server()
            refresh_motd()
            refresh_feed()
            refresh_chat_off_marker()
        end)
        if not ok then log_line("poll error: " .. tostring(err)) end
        run_on_game_thread(function()
            local ok2, err2 = pcall(render_tick)
            if not ok2 then log_line("render error: " .. tostring(err2)) end
        end)
        return false
    end)
    log_line("LoopAsync poll started (" .. POLL_INTERVAL_MS .. "ms)")

    -- Faster cosmetic tick: drive the typing-cursor repaint + fadeout/visibility
    -- without waiting a full second. Cheap — only touches the game thread when
    -- typing or when visibility needs to change.
    LoopAsync(250, function()
        -- v0.6.1 FAILSAFE: typing must never leave you stuck with movement
        -- frozen. If no keystroke for 15s while the box is open, auto-close +
        -- unfreeze. Keyed to typing.lastKeyAt (bumped by open_typing + every
        -- keystroke via repaint_input) — NOT render.LastActivity, which every
        -- INCOMING message also bumps, so on a busy server the failsafe never
        -- fired and a silently-open box kept movement frozen until Esc.
        -- v0.8.2 MUST-FIX: cancel_typing -> close_typing -> UnfreezeMovement
        -- runs reflected calls (SetIgnoreMoveInput, MaxWalkSpeed/MaxSwimSpeed,
        -- EnableInput, FlushPressedKeys) that are an UNCATCHABLE native AV off
        -- the game thread. Every other typing path hops to the game thread;
        -- this hot auto-fire path must too.
        if typing.active and (os.clock() - typing.lastKeyAt) > 15.0 then
            run_on_game_thread(function() pcall(cancel_typing) end)
        end
        run_on_game_thread(function()
            pcall(function()
                -- Drive whichever surface is live (rich panel OR watermark).
                local panelLive = (panel_valid ~= nil) and panel_valid() == true
                if not panelLive and not render.Reparented then return end
                if render.dirty or typing.active then render_tick() else apply_visibility() end
            end)
        end)
        return false
    end)
else
    -- Synchronous fallback (dev / unit-test harness): one pass.
    pcall(function()
        refresh_server(); refresh_motd(); refresh_feed(); refresh_chat_off_marker()
    end)
    log_line("LoopAsync not available — synchronous one-shot only")
end

-- Export for the smoke harness / unit tests (drive the outbox + parser
-- without needing the game running).
return {
    version       = MOD_VERSION,
    queue_outbox  = queue_outbox,
    parse_messages = parse_messages,
    OUTBOX_DIR    = OUTBOX_DIR,
}
