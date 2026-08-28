<#
.SYNOPSIS
    Install or update the RFID Reader Service on Windows.

.DESCRIPTION
    The Windows counterpart to deploy/install.sh. Works from a self-contained
    publish folder, so the target PC needs no .NET, no SDK and no git - which is
    the normal state of a customer machine.

    A card reader is often wired straight to the weigh PC rather than to a Pi;
    this is the installer for that case.

    Installs the Windows service with automatic startup, points it at the web
    app, and verifies it actually came up and took the settings.

    Safe to re-run: on an existing install it stops the service, preserves the
    database, copies the new binaries and starts it again.

.PARAMETER WebUrl
    Base URL of the BasicWeigh web app, e.g. https://valleyag.scaledata.net
    Must match the web app's ACTUAL scheme and port. A wrong URL puts the
    service into an endless "Connection refused" reconnect loop.

.PARAMETER ServiceId
    Identifies this service instance to the web app. Kiosks address a reader as
    "serviceId:readerId", so this name ends up in the kiosk configuration.

.PARAMETER SerialPort
    COM port the card reader is on, e.g. COM3. Seeds one reader on a fresh
    install so the service comes up reading without any API calls. Omit it and
    no reader is seeded - add it from the web app instead.

.PARAMETER Port
    Local port for the Swagger/diagnostic API. Default 5250.

    Each Foundation service owns its own port (see docs/service-ports.md), so
    this should not collide on a normal install. If something else on the PC has
    5250, use -Port 5251. The installer checks first and names the holder.

.PARAMETER InstallDir
    Where the service is installed. Default C:\Services\RfidReaderService

.PARAMETER ResetDb
    Delete the existing database and start clean. This DESTROYS the reader
    configuration and the serial port settings. A timestamped backup is taken
    first regardless.

.PARAMETER SkipUrlCheck
    Install even if the web app's SignalR hub does not answer cleanly. Only
    needed when the hub is deliberately behind something this probe cannot
    satisfy - a redirect here normally means the URL is wrong.

.EXAMPLE
    .\install.ps1 -WebUrl https://valleyag.scaledata.net

.EXAMPLE
    .\install.ps1 -WebUrl https://valleyag.scaledata.net -SerialPort COM3 -ServiceId scalehouse
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$WebUrl,

    [string]$ServiceId = "",
    [string]$SerialPort = "",
    [int]$Port = 5250,
    [string]$InstallDir = "C:\Services\RfidReaderService",
    [string]$ServiceName = "RfidReaderService",
    [switch]$ResetDb,
    [switch]$SkipUrlCheck
)

$ErrorActionPreference = "Stop"

function Step($n, $msg) { Write-Host "[$n/7] $msg" -ForegroundColor Cyan }
function Ok($msg)       { Write-Host "      $msg" -ForegroundColor Green }
function Note($msg)     { Write-Host "      $msg" -ForegroundColor Gray }
function Warn($msg)     { Write-Host "      $msg" -ForegroundColor Yellow }
function Die($msg)      { Write-Host ""; Write-Host "ERROR: $msg" -ForegroundColor Red; exit 1 }

Write-Host ""
Write-Host "==========================================" -ForegroundColor White
Write-Host "  RFID Reader Service - Windows installer" -ForegroundColor White
Write-Host "==========================================" -ForegroundColor White
Write-Host ""

# ---------------------------------------------------------------- preflight --
# Arguments and files first, elevation last: a typo should fail immediately and
# in any prompt, rather than only after the operator re-opens one as admin.

# Catch the wrong-URL mistake here rather than after a silent reconnect loop.
if ($WebUrl -notmatch '^https?://') {
    Die "WebUrl must start with http:// or https:// - got '$WebUrl'"
}
$WebUrl = $WebUrl.TrimEnd('/')

if ($Port -lt 1 -or $Port -gt 65535) { Die "Port must be 1-65535 - got $Port" }

# A Linux device path here is the copy-paste-from-the-Pi-instructions mistake.
# It installs cleanly and never reads a card, so refuse it now.
if ($SerialPort -and $SerialPort -notmatch '^COM\d+$') {
    Die "SerialPort must be a COM port like COM3 - got '$SerialPort'"
}

$appSource = Join-Path $PSScriptRoot "app"
if (-not (Test-Path $appSource)) { $appSource = $PSScriptRoot }
$exeSource = Join-Path $appSource "RfidReaderService.exe"
if (-not (Test-Path $exeSource)) {
    Die "RfidReaderService.exe not found. Expected in '$appSource'. Run this from the unzipped package folder."
}

$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Die "This must run from an ADMIN PowerShell. Creating a Windows service needs it."
}

# ---- Service ID -------------------------------------------------------------
# Default to the machine name so every install lands on the web app's Card
# Readers page under a distinct, recognisable identifier - "default" on three
# boxes is indistinguishable, and a kiosk addresses a reader by this name.
if ([string]::IsNullOrWhiteSpace($ServiceId)) {
    $isInteractive = [Environment]::UserInteractive -and -not [Console]::IsInputRedirected
    if ($isInteractive) {
        Write-Host ""
        Write-Host "Enter a Service ID for this card reader box." -ForegroundColor Yellow
        Write-Host "  Kiosks are mapped to a reader as 'serviceId:readerId', so this name"
        Write-Host "  is what you will pick from on the web app."
        Write-Host "  Press Enter to use this computer's name: $env:COMPUTERNAME" -ForegroundColor DarkGray
        $answer = Read-Host "ServiceId"
        if ([string]::IsNullOrWhiteSpace($answer)) {
            $ServiceId = $env:COMPUTERNAME
            Write-Host "  Using: $ServiceId" -ForegroundColor Cyan
        } else {
            $ServiceId = $answer.Trim()
        }
        Write-Host ""
    } else {
        # Unattended rollouts still land uniquely without passing -ServiceId.
        $ServiceId = $env:COMPUTERNAME
    }
}

Write-Host "  Web app     : $WebUrl"
Write-Host "  Service ID  : $ServiceId"
Write-Host "  API port    : $Port"
Write-Host "  Install dir : $InstallDir"
Write-Host "  Source      : $appSource"
if ($SerialPort) { Write-Host "  Reader port : $SerialPort (seeded on a fresh database)" }
else             { Write-Host "  Reader port : none - add the reader from the web app" }
if ($ResetDb)    { Write-Host "  Database    : RESET (existing config will be destroyed)" -ForegroundColor Yellow }

# A mistyped URL is the classic failure: the service installs cleanly and then
# reconnects forever against nothing. Say so now, while someone is watching.
#
# Probe the SignalR negotiate endpoint the service will actually use, not the
# site root, and do NOT follow redirects. Both details matter. A plain GET of
# the root follows a redirect and reports a cheerful "HTTP 200" for a URL the
# service cannot use: negotiate is a POST, an http->https 301 downgrades it to
# GET, and the hub answers 405 Method Not Allowed forever. That install looks
# perfect and never connects.
# HttpWebRequest rather than Invoke-WebRequest: this runs under Windows
# PowerShell 5.1, where -MaximumRedirection 0 throws a bare
# InvalidOperationException carrying no response, so the redirect this exists to
# catch is invisible. With AllowAutoRedirect off, a 3xx comes back as an ordinary
# response and the Location header survives.
function Test-HubEndpoint {
    param([string]$BaseUrl)

    $uri = "$BaseUrl/scaleHub/negotiate?negotiateVersion=1"
    try {
        [Net.ServicePointManager]::SecurityProtocol =
            [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    } catch { }

    $resp = $null
    try {
        $req = [Net.HttpWebRequest]::Create($uri)
        $req.Method            = "POST"
        $req.AllowAutoRedirect = $false
        $req.Timeout           = 10000
        $req.ContentLength     = 0
        $resp = $req.GetResponse()
        return @{ Status = [int]$resp.StatusCode; Location = $resp.Headers["Location"] }
    } catch [Net.WebException] {
        # 4xx/5xx still throw; the response carries the code we want to report.
        if ($_.Exception.Response) {
            $r = $_.Exception.Response
            return @{ Status = [int]$r.StatusCode; Location = $r.Headers["Location"] }
        }
        return @{ Status = 0; Error = $_.Exception.Message }
    } catch {
        return @{ Status = 0; Error = $_.Exception.Message }
    } finally {
        if ($resp) { $resp.Close() }
    }
}

if ($SkipUrlCheck) {
    Write-Host "  Hub check   : skipped (-SkipUrlCheck)" -ForegroundColor Yellow
} else {
    $hub = Test-HubEndpoint $WebUrl

    if ($hub.Status -ge 300 -and $hub.Status -lt 400) {
        # The redirect target is the URL that actually works, so hand it over
        # rather than making someone guess which scheme or host was meant.
        $suggest = $WebUrl
        if ($hub.Location -and $hub.Location -match '^https?://[^/]+') {
            $suggest = $Matches[0]
        } elseif ($WebUrl -like 'http://*') {
            $suggest = $WebUrl -replace '^http://', 'https://'
        }
        Write-Host "  Hub check   : REDIRECT (HTTP $($hub.Status))" -ForegroundColor Red
        Write-Host ""
        Write-Host "  $WebUrl redirects to $($hub.Location)" -ForegroundColor Yellow
        Write-Host "  SignalR negotiates with a POST, and a redirect turns that into a GET," -ForegroundColor Yellow
        Write-Host "  which the hub rejects with 405. The service would install cleanly and" -ForegroundColor Yellow
        Write-Host "  then reconnect forever." -ForegroundColor Yellow
        Write-Host ""
        Die "Re-run with the URL the site actually serves:`n         INSTALL.bat $suggest`n`n       Use -SkipUrlCheck to install anyway."
    }
    elseif ($hub.Status -eq 200) {
        Write-Host "  Hub check   : ok (negotiate answered 200)" -ForegroundColor Green
    }
    elseif ($hub.Status -eq 401 -or $hub.Status -eq 403) {
        Write-Host "  Hub check   : reachable, but negotiate returned $($hub.Status)" -ForegroundColor Yellow
        Write-Host "                The hub is there; it refused this unauthenticated probe." -ForegroundColor Yellow
    }
    elseif ($hub.Status -eq 404) {
        Write-Host "  Hub check   : NO - no /scaleHub at $WebUrl (HTTP 404)" -ForegroundColor Yellow
        Write-Host "                Right server, wrong app? The service will retry forever." -ForegroundColor Yellow
        Write-Host "                Ctrl+C now if that URL is wrong." -ForegroundColor Yellow
    }
    elseif ($hub.Status -eq 0) {
        Write-Host "  Hub check   : NO - $WebUrl did not answer within 10s." -ForegroundColor Yellow
        Write-Host "                $($hub.Error)" -ForegroundColor Yellow
        Write-Host "                The service will install and retry forever. If that URL is" -ForegroundColor Yellow
        Write-Host "                wrong, Ctrl+C now and re-run with the right one." -ForegroundColor Yellow
    }
    else {
        Write-Host "  Hub check   : unexpected HTTP $($hub.Status) from negotiate" -ForegroundColor Yellow
    }
}
Write-Host ""

$dbPath      = Join-Path $InstallDir "rfidreaderservice.db"
$existingSvc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

# ------------------------------------------------------------- 1. stop svc --
Step 1 "Stopping service (if running)..."
if ($existingSvc) {
    if ($existingSvc.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        # The service holds its own .exe; copying before the handle is released
        # fails with a file lock, so wait for Stopped rather than assuming.
        $waited = 0
        while ((Get-Service -Name $ServiceName).Status -ne 'Stopped' -and $waited -lt 30) {
            Start-Sleep -Seconds 1; $waited++
        }
        if ((Get-Service -Name $ServiceName).Status -ne 'Stopped') {
            Die "Service would not stop after ${waited}s. Stop it by hand and re-run."
        }
        Ok "Stopped after ${waited}s."
    } else { Ok "Already stopped." }
    # Windows can hold the file handle briefly after the status flips.
    Start-Sleep -Seconds 3
} else {
    Note "Not installed yet - this is a fresh install."
}

# The Windows service is not the only thing that can hold these binaries. A copy
# started by hand from a console - the normal way to watch frames while
# commissioning a reader - keeps RfidReaderService.exe locked, and "Already
# stopped" above refers only to the service, so nothing here would notice it.
# The copy in step 3 then retries against a file that is never going to be
# released, which reads as a dead installer.
# Win32_Process rather than Get-Process: ExecutablePath is what decides whether a
# process is holding THIS install, and Get-Process leaves .Path empty for
# processes the caller cannot open. That is not supposed to happen once elevated,
# but a silently empty path here would skip the very instance we came to find.
function Get-InstalledInstances {
    param([string]$Dir)
    return @(Get-CimInstance Win32_Process -Filter "Name='RfidReaderService.exe'" `
                 -ErrorAction SilentlyContinue |
             Where-Object { $_.ExecutablePath -and $_.ExecutablePath -like "$Dir\*" })
}

$stray = Get-InstalledInstances $InstallDir
if ($stray.Count -gt 0) {
    foreach ($p in $stray) {
        Warn "Running outside the service: pid $($p.ProcessId) - $($p.ExecutablePath)"
        Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Seconds 2
    $stillThere = Get-InstalledInstances $InstallDir
    if ($stillThere.Count -gt 0) {
        Die ("Could not stop the copy running from $InstallDir (pid " +
             (($stillThere | ForEach-Object { $_.ProcessId }) -join ", ") +
             "). Close the window running it and re-run.")
    }
    Ok "Stopped $($stray.Count) stray instance(s)."
}

# A copy running from somewhere else cannot lock this install, so it is not
# stopped - but it will fight for port $Port and the same COM port, so say so.
$elsewhere = @(Get-CimInstance Win32_Process -Filter "Name='RfidReaderService.exe'" `
                   -ErrorAction SilentlyContinue |
               Where-Object { -not $_.ExecutablePath -or $_.ExecutablePath -notlike "$InstallDir\*" })
foreach ($p in $elsewhere) {
    $where = $p.ExecutablePath
    if (-not $where) { $where = "(path unavailable)" }
    Warn "Another copy is running from $where (pid $($p.ProcessId)) - it may hold port $Port."
}

# ------------------------------------------------------------ 2. backup db --
Step 2 "Backing up database..."
if (Test-Path $dbPath) {
    $stamp  = Get-Date -Format "yyyyMMdd-HHmmss"
    $backup = Join-Path ([Environment]::GetFolderPath('Desktop')) "rfidreaderservice-$stamp.db.bak"
    Copy-Item $dbPath $backup -Force
    Ok "Saved to $backup"
} else {
    Note "No existing database - a new one will be created on first start."
}

# ------------------------------------------------------------ 3. copy files --
Step 3 "Copying binaries..."
if (-not (Test-Path $InstallDir)) { New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null }

# The database lives in the application folder and is not part of the publish
# output. Exclude it - and its write-ahead companions, since dropping a stale
# -wal next to a different database risks corrupting it.
# /R and /W are not optional here. Robocopy defaults to a million retries at 30
# seconds each - roughly a year - and the /N* flags above suppress every word of
# it, so one locked file turns this step into a silent hang with no way to tell
# it apart from a crash. Fail in seconds instead and say what is holding the file.
$null = robocopy $appSource $InstallDir /E /NFL /NDL /NJH /NJS /NP /R:2 /W:5 `
    /XF rfidreaderservice.db rfidreaderservice.db-wal rfidreaderservice.db-shm
if ($LASTEXITCODE -ge 8) {
    Warn "robocopy exit code $LASTEXITCODE - some files could not be copied."
    $holding = @(Get-Process -Name "RfidReaderService" -ErrorAction SilentlyContinue)
    if ($holding.Count -gt 0) {
        foreach ($p in $holding) { Warn "Still running: pid $($p.Id) - $($p.Path)" }
        Die "Something is still holding the binaries. Close it and re-run."
    }
    Die ("Copy into $InstallDir failed. Usually a file is locked (antivirus, or an " +
         "open Explorer/console window in that folder). Close what you can and re-run.")
}
Ok "Binaries in place."

if ($ResetDb) {
    Remove-Item "$dbPath","$dbPath-wal","$dbPath-shm" -Force -ErrorAction SilentlyContinue
    Ok "Database reset - it will be recreated from appsettings.json."
}

# -------------------------------------------------------- 4. configure app --
Step 4 "Writing configuration..."
$settingsPath = Join-Path $InstallDir "appsettings.json"
if (-not (Test-Path $settingsPath)) { Die "appsettings.json missing from $InstallDir - the copy did not complete." }

$cfg = Get-Content $settingsPath -Raw | ConvertFrom-Json
if (-not $cfg.Rfid) { $cfg | Add-Member -NotePropertyName Rfid -NotePropertyValue ([pscustomobject]@{}) -Force }
$cfg.Rfid | Add-Member -NotePropertyName ServerUrl -NotePropertyValue $WebUrl    -Force
$cfg.Rfid | Add-Member -NotePropertyName ServiceId -NotePropertyValue $ServiceId -Force
# "+" binds dual-stack (IPv6 and IPv4). "0.0.0.0" is IPv4-ONLY, and Windows
# resolves "localhost" to ::1 first - so an IPv4-only bind leaves the service
# listening yet unreachable by name, which looks exactly like a service that
# started but never came up.
$cfg | Add-Member -NotePropertyName Urls -NotePropertyValue "http://+:$Port" -Force

# The shipped appsettings.json seeds a reader on /dev/ttyUSB0, which is right for
# the Pi this service usually runs on and meaningless here. Left alone, a fresh
# Windows install invents a reader that can never open its port and publishes it
# to the web app's Card Readers page, where it looks like a broken reader rather
# than a phantom one. Seed the real COM port instead, or nothing at all.
if ($SerialPort) {
    $cfg | Add-Member -NotePropertyName Readers -NotePropertyValue @(
        [pscustomobject]@{
            ReaderId            = "$ServiceId-reader"
            DisplayName         = "$ServiceId card reader"
            ReaderModel         = "AWID Sentinel-Prox SP-6820"
            SerialPortName      = $SerialPort
            BaudRate            = 9600
            DataBits            = 8
            Parity              = "None"
            StopBits            = 1
            Format              = "Wiegand26"
            IncludeFacilityCode = $false
            MinLength           = 4
            DebounceMs          = 3000
            IdleFrameMs         = 60
            TimeoutMs           = 500
            Active              = $true
        }
    ) -Force
} else {
    $cfg | Add-Member -NotePropertyName Readers -NotePropertyValue @() -Force
}

$cfg | ConvertTo-Json -Depth 10 | Set-Content $settingsPath -Encoding UTF8
Ok "appsettings.json updated."

# ----------------------------------------------------- 5. install service --
Step 5 "Installing Windows service..."
$binPath = Join-Path $InstallDir "RfidReaderService.exe"
if ($existingSvc) {
    # Correct the path in case the install location moved, and make sure it
    # still starts on boot - a previous manual `sc create` may have omitted that.
    & sc.exe config $ServiceName binPath= "`"$binPath`"" start= auto | Out-Null
    Ok "Existing service updated (start = automatic)."
} else {
    & sc.exe create $ServiceName binPath= "`"$binPath`"" start= auto DisplayName= "RFID Reader Service" | Out-Null
    if ($LASTEXITCODE -ne 0) { Die "sc create failed with code $LASTEXITCODE" }
    & sc.exe description $ServiceName "Reads HID / prox cards from an RS-232 card reader and posts card presentations to BasicWeigh via SignalR." | Out-Null
    # Restart on crash: 5s, 15s, then every 60s. A weighbridge PC is rarely
    # watched, so an unattended recovery beats waiting for someone to notice.
    & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
    Ok "Service created (start = automatic, restarts on failure)."
}

# ---------------------------------------------------------- 6. start & wait --
Step 6 "Starting service..."

# Check the port BEFORE starting. Kestrel dies with "address already in use",
# which surfaces only as a service that starts and immediately stops - easy to
# misread as the installer hanging.
$portOwner = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
             Select-Object -First 1
if ($portOwner) {
    $op = Get-Process -Id $portOwner.OwningProcess -ErrorAction SilentlyContinue
    if ($op -and $op.Path -notlike "$InstallDir*") {
        # Name the holder rather than leaving a bare port clash. The Web Print
        # Service defaults to 5230, so seeing it here means something moved it.
        $hint = ""
        if ($op.Name -like "PiPrintService*") {
            $hint = " That is the Web Print Service; it defaults to 5230, so something has moved it."
        }
        Die ("Port $Port is already in use by '$($op.Name)' (pid $($op.Id))." + $hint +
             " Re-run with -Port 5251.")
    }
}

Start-Service -Name $ServiceName

# First start on a fresh install is the slow one: a new database, and the
# antivirus scanning a few hundred just-copied files. Wait generously, but stop
# early if the service dies - there is nothing to wait for then.
$health  = $null
$waitFor = 90
for ($i = 1; $i -le $waitFor; $i++) {
    Start-Sleep -Seconds 1
    try {
        $health = Invoke-RestMethod "http://127.0.0.1:$Port/api/health" -TimeoutSec 2
        break
    } catch { }

    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -ne 'Running') {
        Warn "The service stopped on its own after ${i}s - it failed during startup."
        break
    }
    if ($i % 15 -eq 0) { Note "still waiting... ${i}s" }
}

if (-not $health) {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    Write-Host ""
    Warn "The API never answered on port $Port."
    Warn "Service status : $(if ($svc) { $svc.Status } else { 'not found' })"

    $owner = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
             Select-Object -First 1
    if ($owner) {
        $op = Get-Process -Id $owner.OwningProcess -ErrorAction SilentlyContinue
        Warn "Port $Port is held by: $($op.Name) (pid $($op.Id))"
    } else {
        Warn "Nothing is listening on port $Port."
    }

    # The actual exception, rather than making the operator go looking for it.
    Write-Host ""
    Warn "Recent application errors:"
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Level = 1, 2;
                                     StartTime = (Get-Date).AddMinutes(-5) } -ErrorAction SilentlyContinue |
        Select-Object -First 5 |
        ForEach-Object { Write-Host ("        " + ($_.Message -split "`n")[0]) -ForegroundColor Yellow }

    Write-Host ""
    Warn "To see the real error, run it in the foreground:"
    Warn "  `"$InstallDir\RfidReaderService.exe`""
    Die "Aborting before settings are applied - the service is not healthy."
}
Ok "Healthy - $($health.service) v$($health.version)."

# -------------------------------------------------------- 7. apply settings --
Step 7 "Applying settings..."
# Done through the API, not appsettings.json, on purpose: the config file only
# seeds the database while ServerUrl is still the factory default, so on an
# existing install editing appsettings.json alone would change nothing.
$body = @{ serviceId = $ServiceId; serverUrl = $WebUrl; signalRHub = "/scaleHub" } | ConvertTo-Json
try {
    $applied = Invoke-RestMethod "http://127.0.0.1:$Port/api/settings" -Method Put `
        -ContentType "application/json" -Body $body -TimeoutSec 10
    Ok "ServiceId = $($applied.serviceId)"
    Ok "ServerUrl = $($applied.serverUrl)"
} catch {
    Die "Could not apply settings: $($_.Exception.Message)"
}

# The COM ports this PC actually offers, so a wrong -SerialPort is obvious now
# rather than after someone waves a card at a reader that never answers.
try {
    $ports = @(Invoke-RestMethod "http://127.0.0.1:$Port/api/serialports" -TimeoutSec 10)
    if ($ports.Count -gt 0) {
        Ok "Serial ports here: $($ports -join ', ')"
        if ($SerialPort -and $ports -notcontains $SerialPort) {
            Warn "$SerialPort is NOT in that list - check the reader's USB/serial adapter."
        }
    } else {
        Warn "This PC reports no serial ports. Is the reader's adapter plugged in?"
    }
} catch {
    Note "Could not list serial ports: $($_.Exception.Message)"
}

Write-Host ""
Write-Host "==========================================" -ForegroundColor Green
Write-Host "  Installed and running" -ForegroundColor Green
Write-Host "==========================================" -ForegroundColor Green
Write-Host ""
Write-Host "  Swagger : http://localhost:$Port/swagger"
Write-Host "  Health  : http://localhost:$Port/api/health"
Write-Host "  Status  : http://localhost:$Port/api/status"
Write-Host ""
Write-Host "  Confirm it reached the web app - this box should appear under"
Write-Host "  Setup -> Options -> Card Readers at $WebUrl"
Write-Host ""
if (-not $SerialPort) {
    Write-Host "  No reader was seeded. Add one on the Card Readers page, then map" -ForegroundColor Yellow
    Write-Host "  the kiosk to '${ServiceId}:<readerId>'." -ForegroundColor Yellow
    Write-Host ""
}
Write-Host "  Present a card and watch the frames arrive, parsed or not:"
Write-Host "    http://localhost:$Port/api/readers/<readerId>/frames"
Write-Host ""
Write-Host "  Swagger listens on all interfaces. Windows Firewall will block it"
Write-Host "  from other machines unless you add an inbound rule for port $Port."
Write-Host ""
