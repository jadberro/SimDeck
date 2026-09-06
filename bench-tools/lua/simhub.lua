-- simhub.lua : FSUIPC7 side of SimHub
--
-- Install:
--   1. copy this file into your FSUIPC7 folder
--   2. add to FSUIPC7.ini under [Auto] :   1=Lua simhub
--   3. point WATCHLIST below at simhub/lua/simhub_lvars.txt
--
-- The hub writes the watchlist file. You should never need to edit the
-- LVAR list in here.

local WATCHLIST   = [[C:\SimHub\simhub\lua\simhub_lvars.txt]]
local HUB_IP      = "127.0.0.1"
local TX_PORT     = 27510    -- lua -> hub
local RX_PORT     = 27511    -- hub -> lua (writes)
local SEND_MS     = 33       -- ~30 Hz
local RELOAD_MS   = 5000     -- re-read the watchlist this often

local lvars   = {}
local tx      = com.udpconnect(HUB_IP, TX_PORT)
local ac_sent = 0

------------------------------------------------------------------
-- watchlist
------------------------------------------------------------------

function reload_watchlist()
    local fh = io.open(WATCHLIST, "r")
    if not fh then return end
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
-- send loop
------------------------------------------------------------------

function send_values()
    if #lvars == 0 then return end

    local parts = {}

    -- aircraft title every ~1s so the hub can pick a profile
    ac_sent = ac_sent + 1
    if ac_sent >= 30 then
        ac_sent = 0
        local title = ipc.readSTR(0x3D00, 256) or ""
        title = title:gsub("[;=\n\r]", " ")
        parts[#parts + 1] = "__AC=" .. title
    end

    for i = 1, #lvars do
        local name = lvars[i]
        local v = ipc.readLvar(name)
        if v ~= nil then
            parts[#parts + 1] = string.format("%s=%.4f", name, v)
        end
    end

    if #parts > 0 then
        -- keep each datagram well under the MTU
        local buf = ""
        for i = 1, #parts do
            if #buf + #parts[i] + 1 > 1200 then
                com.write(tx, buf)
                buf = ""
            end
            buf = buf .. parts[i] .. ";"
        end
        if #buf > 0 then com.write(tx, buf) end
    end
end

------------------------------------------------------------------
-- writes from the hub
------------------------------------------------------------------

function on_hub_write(handle, data)
    local name, val = data:match("^([^=]+)=(.+)$")
    if name and val then
        ipc.writeLvar(name, tonumber(val) or 0)
    end
end

------------------------------------------------------------------

reload_watchlist()

local rx = com.udpserver("0.0.0.0", RX_PORT, 0, "on_hub_write")

event.timer(SEND_MS, "send_values")
event.timer(RELOAD_MS, "reload_watchlist")

ipc.log("SimHub lua started")
