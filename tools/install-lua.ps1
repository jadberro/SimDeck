# Installs simdeck.lua into FSUIPC7 and registers it to run automatically.
#
# Does not assume where FSUIPC7 lives: asks the running process first, then
# looks in the usual places, then asks you.

$ErrorActionPreference = 'Stop'

function Say($m, $c = 'Gray') { Write-Host $m -ForegroundColor $c }

$INSTALLER_VERSION = 4

Say ""
Say "  SimDeck - FSUIPC bridge installer  (v$INSTALLER_VERSION)" 'White'
Say "  ----------------------------------------"
Say ""

# ---- locate the script we are installing --------------------------------
$lua = Join-Path $PSScriptRoot "..\bench-tools\lua\simdeck.lua"
if (-not (Test-Path $lua)) {
    Say "  Cannot find simdeck.lua next to this script." 'Red'
    Say "  Expected: $lua"
    exit 1
}
$lua = (Resolve-Path $lua).Path

# ---- locate FSUIPC7 ------------------------------------------------------
$folder = $null

# 1. running process is the most reliable answer
$proc = Get-Process -Name FSUIPC7 -ErrorAction SilentlyContinue | Select-Object -First 1
if ($proc) {
    $folder = Split-Path $proc.Path -Parent
    Say "  Found running FSUIPC7: $folder" 'Green'
}

# 2. the usual places
if (-not $folder) {
    $guesses = @("C:\FSUIPC7", "D:\FSUIPC7", "E:\FSUIPC7",
                 "C:\Program Files\FSUIPC7", "C:\Program Files (x86)\FSUIPC7")
    foreach ($g in $guesses) {
        if (Test-Path (Join-Path $g "FSUIPC7.exe")) {
            $folder = $g
            Say "  Found FSUIPC7: $folder" 'Green'
            break
        }
    }
}

# 3. ask
if (-not $folder) {
    Say "  Could not find FSUIPC7 automatically." 'Yellow'
    Say "  It is the folder containing FSUIPC7.exe."
    Say ""
    $folder = Read-Host "  Paste the full path"
    $folder = $folder.Trim('"').Trim()
}

if (-not (Test-Path (Join-Path $folder "FSUIPC7.exe"))) {
    Say ""
    Say "  FSUIPC7.exe is not in $folder" 'Red'
    Say "  Tip: right-click FSUIPC7 in the taskbar, Properties, and look at" 
    Say "  the Start in / Target path."
    exit 1
}

# ---- licence check -------------------------------------------------------
#
# Only worth mentioning if the log is recent. A log left over from before you
# registered will otherwise produce a confident warning that is simply wrong,
# which is worse than saying nothing.
$log = Join-Path $folder "FSUIPC7.log"
if (Test-Path $log) {
    $age = (Get-Date) - (Get-Item $log).LastWriteTime
    if ($age.TotalDays -lt 7) {
        $head = Get-Content $log -TotalCount 80 -ErrorAction SilentlyContinue
        $looksUnregistered = $head -match "not user registered"
        $looksRegistered = $head -match "User Name=""[^""]+"""

        if ($looksUnregistered -and -not $looksRegistered) {
            Say ""
            Say "  Note: the FSUIPC log from $([int]$age.TotalHours)h ago says" 'Yellow'
            Say "  'not user registered'. Lua scripting needs a licence."
            Say "  If you have registered since, ignore this."
            Say ""
        }
    }
}

# ---- copy the script -----------------------------------------------------
$srcVer = (Select-String -Path $lua -Pattern 'local VERSION = (\d+)' |
           Select-Object -First 1).Matches.Groups[1].Value
$dest = Join-Path $folder "simdeck.lua"

$oldVer = "none"
if (Test-Path $dest) {
    $m = Select-String -Path $dest -Pattern 'local VERSION = (\d+)' | Select-Object -First 1
    if ($m) { $oldVer = "v" + $m.Matches.Groups[1].Value } else { $oldVer = "pre-versioning" }
}

# FSUIPC7 holds this file open while running, and a silent failure here is
# exactly how an old script keeps running while you assume it was replaced.
try {
    Copy-Item $lua $dest -Force -ErrorAction Stop
}
catch {
    Say ""
    Say "  Could not overwrite $dest" 'Red'
    Say "  FSUIPC7 is probably running and holding the file open."
    Say "  Close FSUIPC7 completely, then run this again."
    Say ""
    exit 1
}

$check = (Select-String -Path $dest -Pattern 'local VERSION = (\d+)' |
          Select-Object -First 1).Matches.Groups[1].Value
if ($check -ne $srcVer) {
    Say "  Copy did not take - $dest still reports v$check" 'Red'
    exit 1
}
Say "  Installed simdeck.lua v$srcVer  (was: $oldVer)" 'Green'
Say "  -> $dest"

# ---- register it in the ini ---------------------------------------------
$ini = Join-Path $folder "FSUIPC7.ini"
if (-not (Test-Path $ini)) {
    Say "  FSUIPC7.ini not found. Run FSUIPC7 once, then run this again." 'Red'
    exit 1
}

$backup = Join-Path $folder ("FSUIPC7.ini.simdeck-backup-" +
                             (Get-Date -Format "yyyyMMdd-HHmmss"))
Copy-Item $ini $backup
Say "  Backed up the ini to $(Split-Path $backup -Leaf)" 'Green'

$lines = Get-Content $ini

if ($lines -match '^\s*\d+\s*=\s*Lua\s+simdeck\s*$') {
    Say "  Already registered in [Auto] - nothing to change." 'Green'
}
else {
    $autoIdx = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^\s*\[Auto\]\s*$') { $autoIdx = $i; break }
    }

    if ($autoIdx -lt 0) {
        # no [Auto] section: append one
        $lines += ""
        $lines += "[Auto]"
        $lines += "1=Lua simdeck"
        Say "  Added an [Auto] section with 1=Lua simdeck" 'Green'
    }
    else {
        # find the next free number inside the existing section
        $used = @()
        $end = $lines.Count
        for ($i = $autoIdx + 1; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match '^\s*\[') { $end = $i; break }
            if ($lines[$i] -match '^\s*(\d+)\s*=') { $used += [int]$Matches[1] }
        }
        $n = 1
        while ($used -contains $n) { $n++ }

        $new = @()
        $new += $lines[0..($end - 1)]
        $new += "$n=Lua simdeck"
        if ($end -lt $lines.Count) { $new += $lines[$end..($lines.Count - 1)] }
        $lines = $new
        Say "  Added $n=Lua simdeck to the existing [Auto] section" 'Green'
    }

    Set-Content -Path $ini -Value $lines -Encoding ASCII
}

Say ""
Say "  Done." 'White'
Say ""
Say "  Next:"
Say "    1. Restart FSUIPC7"
Say "    2. Load your aircraft and sit in the cockpit, ready to fly."
Say "       Auto scripts do not start until a flight is loaded."
Say "    3. Open SimDeck. The Variables page should show 'bridge: live'."
Say ""
Say "  If it does not, open FSUIPC7.log and look for:"
Say "    SimDeck lua started"
Say ""
