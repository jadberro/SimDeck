-- simdeck.lua : FSUIPC7 side of SimDeck
--
-- Install with tools\install-lua.cmd, or by hand:
--   1. copy this file next to FSUIPC7.exe
--   2. in FSUIPC7.ini, under [Auto] :   1=Lua simdeck
--   3. restart FSUIPC7 and load a flight, sitting in the cockpit
--
-- Design notes, both learned the hard way
-- ---------------------------------------
-- 1. Values go to a FILE, not a socket. FSUIPC's Lua com library is thinly
--    documented and an earlier version used socket calls that may not exist
--    in every build. io.open and ipc.readLvar certainly do.
--
-- 2. This uses a plain loop with ipc.sleep, NOT event.timer. With timers the
--    script wrote its status exactly once and then went quiet: either the
--    callbacks never fired, or the first one threw and silently killed the
--    thread with nothing in any log. A loop is one less mechanism to trust,
--    and the pcall below means a single bad variable cannot stop it.

-- Bumped on every change. SimDeck displays it, so "still slow" can never
-- again be ambiguous about whether the fix is actually installed.
local VERSION = 4

local DIR = os.getenv("LOCALAPPDATA") .. "\\SimDeck\\bridge"
local STATUS  = DIR .. "\\status.txt"
local WATCH   = DIR .. "\\lvars.txt"
local COMMAND = DIR .. "\\command.txt"
local SCAN    = DIR .. "\\scan.txt"

local TICK_MS     = 33    -- 30 Hz
local RELOAD_EVERY = 60   -- ticks between watchlist re-reads (~2s)
local CMD_EVERY    = 9    -- ticks between command checks (~300ms)

local lvars = {}
local seq = 0
local last_error = ""

-- Timing, so we can see WHERE the time goes rather than infer it.
-- Measured at 1 write/s with six variables, which points at readLvar
-- blocking on a round trip rather than at the sleep interval.
local ms_total, ms_read, ms_io, ms_sleep = 0, 0, 0, 0

-- The status file is opened ONCE and rewritten in place.
--
-- Measured 422ms to write a kilobyte, which is not disk - it is antivirus
-- scanning the file on every close. Thirty writes a second means thirty
-- scans a second. Holding the handle open and seeking to the start avoids
-- the close entirely.
--
-- The record is padded to a fixed length so a shorter update cannot leave
-- stale text from a longer one behind; the reader stops at #END anyway.
local PAD_TO = 8192
local status_fh = nil

-- Which clock we got matters when reading the numbers: os.clock measures CPU
-- time, so a thread blocked on a round trip barely advances it and the
-- timings understate reality. Report which one is in use.
local clock_kind = "cpu"
do
    local ok, t = pcall(function() return ipc.elapsedtime() end)
    if ok and type(t) == "number" then clock_kind = "real" end
end

local function now_ms()
    if clock_kind == "real" then
        local ok, t = pcall(function() return ipc.elapsedtime() end)
        if ok and type(t) == "number" then return t end
    end
    return (os.clock() * 1000)
end

------------------------------------------------------------------

local cached_title = ""

-- Re-read rarely. It cannot change without a reload, and if readSTR is also
-- a round trip then doing it every tick is pure waste.
local function aircraft_title(force)
    if force or cached_title == "" then
        local ok, t = pcall(function() return ipc.readSTR(0x3D00, 256) end)
        if ok and t then cached_title = (t:gsub("[\r\n]", " ")) end
    end
    return cached_title
end

local function reload_watchlist()
    local fh = io.open(WATCH, "r")
    if not fh then
        lvars = {}
        return
    end
    local fresh = {}
    for line in fh:lines() do
        line = line:match("^%s*(.-)%s*$")
        if line ~= "" and line:sub(1, 1) ~= "#" then
            fresh[#fresh + 1] = line
        end
    end
    fh:close()
    lvars = fresh
end

------------------------------------------------------------------
-- status file
--
-- Rewritten in place each tick. The reader skips any file missing the
-- trailing #END, so a half-written file is ignored rather than parsed as a
-- set of variables that suddenly vanished.
------------------------------------------------------------------

local function write_status()
    seq = seq + 1

    -- Read everything FIRST, timing it, then write. Holding the file open
    -- across blocking reads also meant SimDeck hit a partial file more often.
    local t0 = now_ms()
    local vals = {}
    for i = 1, #lvars do
        local name = lvars[i]
        local okv, v = pcall(ipc.readLvar, name)
        if okv and v ~= nil then
            vals[#vals + 1] = name .. "=" .. string.format("%.4f", v)
        end
    end
    ms_read = now_ms() - t0

    local t1 = now_ms()

    if not status_fh then
        -- r+b keeps the existing file; w+b creates it the first time.
        status_fh = io.open(STATUS, "r+b") or io.open(STATUS, "w+b")
        if not status_fh then
            ipc.log("SimDeck: cannot open " .. STATUS)
            return
        end
    end

    local body = {}
    body[#body + 1] = "#SEQ " .. seq
    body[#body + 1] = "#AC " .. aircraft_title()
    body[#body + 1] = "#N " .. #lvars
    body[#body + 1] = "#MS " .. string.format("%.0f", ms_total)
    body[#body + 1] = "#RD " .. string.format("%.0f", ms_read)
    body[#body + 1] = "#IO " .. string.format("%.0f", ms_io)
    body[#body + 1] = "#CLK " .. clock_kind
    body[#body + 1] = "#VER " .. VERSION
    body[#body + 1] = "#SL " .. string.format("%.0f", ms_sleep)
    if last_error ~= "" then body[#body + 1] = "#ERR " .. last_error end
    for i = 1, #vals do body[#body + 1] = vals[i] end
    body[#body + 1] = "#END"

    local text = table.concat(body, "\n") .. "\n"
    if #text < PAD_TO then
        text = text .. string.rep(" ", PAD_TO - #text)
    end

    local okw = pcall(function()
        status_fh:seek("set", 0)
        status_fh:write(text)
        status_fh:flush()
    end)

    if not okw then
        -- handle went stale, e.g. the folder was recreated
        pcall(function() status_fh:close() end)
        status_fh = nil
    end

    ms_io = now_ms() - t1
end

------------------------------------------------------------------
-- discovery
------------------------------------------------------------------

local function scan_lvars()
    local names = {}

    local got, list = pcall(function() return ipc.getLvarList() end)
    if got and type(list) == "table" then
        for k, v in pairs(list) do
            -- some builds key by name, others by index
            if type(k) == "string" then
                names[#names + 1] = k
            elseif type(v) == "string" then
                names[#names + 1] = v
            end
        end
    else
        ipc.log("SimDeck: ipc.getLvarList not available in this build")
    end

    table.sort(names)

    local fh = io.open(SCAN, "w")
    if not fh then return end
    fh:write("#COUNT ", #names, "\n")
    for i = 1, #names do fh:write(names[i], "\n") end
    fh:write("#END\n")
    fh:close()

    ipc.log("SimDeck: scanned " .. #names .. " lvars")
end

------------------------------------------------------------------

local function poll_commands()
    local fh = io.open(COMMAND, "r")
    if not fh then return end

    local lines = {}
    for line in fh:lines() do
        line = line:match("^%s*(.-)%s*$")
        if line ~= "" then lines[#lines + 1] = line end
    end
    fh:close()
    os.remove(COMMAND)

    for i = 1, #lines do
        local cmd = lines[i]
        if cmd == "SCAN" then
            scan_lvars()
        elseif cmd == "RELOAD" then
            reload_watchlist()
        else
            local name, val = cmd:match("^SET%s+([^=]+)=(.+)$")
            if name and val then
                pcall(ipc.writeLvar, name, tonumber(val) or 0)
            end
        end
    end
end

------------------------------------------------------------------
-- main loop
--
-- Every iteration is wrapped: one bad variable, one locked file, one
-- unexpected nil cannot end the run. The error is logged once and also
-- carried in the status file so SimDeck can show it.
------------------------------------------------------------------

ipc.log("SimDeck lua v" .. VERSION .. " started, clock=" .. clock_kind
        .. ", bridge folder: " .. DIR)

aircraft_title(true)
reload_watchlist()

local tick = 0

while true do
    tick = tick + 1
    local iter_start = now_ms()

    local ok, err = pcall(function()
        if tick % CMD_EVERY == 0 then poll_commands() end
        if tick % RELOAD_EVERY == 0 then reload_watchlist() end
        if tick % 300 == 0 then aircraft_title(true) end
        write_status()
    end)

    if not ok then
        local msg = tostring(err)
        if msg ~= last_error then
            last_error = msg
            ipc.log("SimDeck error: " .. msg)
        end
    elseif last_error ~= "" then
        last_error = ""
        ipc.log("SimDeck: recovered")
    end

    ms_total = now_ms() - iter_start

    -- Sleep only for what is left of the budget. If the reads already ate it,
    -- do not add a fixed delay on top and make things worse.
    local remaining = TICK_MS - ms_total
    if remaining < 1 then remaining = 1 end

    -- Time the sleep itself. If asking for 1ms costs hundreds, the Lua thread
    -- is simply not being scheduled often enough and no amount of tuning the
    -- work inside the loop will help.
    local sleep_start = now_ms()
    ipc.sleep(remaining)
    ms_sleep = now_ms() - sleep_start
end
