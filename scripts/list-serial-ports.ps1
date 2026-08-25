<#
.SYNOPSIS
    List the serial (COM) ports this Windows machine offers, with the adapter
    behind each one.

.DESCRIPTION
    The Windows counterpart to scripts/list-serial-ports.sh. Commissioning a
    scale or card reader means naming the port the hardware is on, and Device
    Manager only tells you half of it — this reports the COM number together
    with the adapter's description and its USB serial number, so you can tell
    two identical FTDI cables apart before typing the port into the setup page.

    Windows pins a COM number to the adapter's serial number in the registry,
    so once assigned it survives a replug — unlike Linux, where the ttyUSB
    number can move and you want the /dev/serial/by-id path instead.

    Emits one object per port, so the output can be filtered or exported:
        .\list-serial-ports.ps1 | Where-Object Description -match FTDI
        .\list-serial-ports.ps1 | Export-Csv ports.csv -NoTypeInformation

.PARAMETER Plain
    Emit just the port names ("COM3"), one per line, for scripting.

.EXAMPLE
    .\scripts\list-serial-ports.ps1

.EXAMPLE
    .\scripts\list-serial-ports.ps1 -Plain
#>

[CmdletBinding()]
param(
    [switch]$Plain
)

$ErrorActionPreference = 'Stop'

# GetPortNames is the authority on which ports exist; the PnP records below only
# add description and serial number, and are missing for legacy on-board ports.
$portNames = [System.IO.Ports.SerialPort]::GetPortNames() | Sort-Object {
    if ($_ -match '(\d+)$') { [int]$matches[1] } else { 0 }
}

if (-not $portNames) {
    Write-Warning "No serial ports found."
    Write-Host ""
    Write-Host "  - USB adapter plugged in? Check Device Manager for a yellow-flagged device;"
    Write-Host "    an adapter with no driver shows under 'Other devices' and gets no COM number."
    Write-Host "  - Adapters that were installed but are unplugged keep their COM number reserved"
    Write-Host "    and do not appear here until plugged back in."
    return
}

if ($Plain) {
    $portNames
    return
}

# Name looks like "USB Serial Port (COM3)". Win32_SerialPort would be the
# obvious query and misses most USB-serial adapters, so go through PnP.
$pnp = @()
try {
    $pnp = Get-CimInstance Win32_PnPEntity -ErrorAction Stop |
        Where-Object { $_.Name -match '\((COM\d+)\)' }
} catch {
    Write-Warning "Could not read device details from WMI: $($_.Exception.Message)"
}

$byPort = @{}
foreach ($device in $pnp) {
    if ($device.Name -match '\((COM\d+)\)') { $byPort[$matches[1]] = $device }
}

foreach ($port in $portNames) {
    $device = $byPort[$port]
    $deviceId = if ($device) { $device.PNPDeviceID } else { $null }

    # FTDI publishes the serial inside the id (FTDIBUS\VID_0403+PID_6001+A9U6VOHBA\0000);
    # plain USB adapters put it in the last segment, and adapters with no serial
    # of their own put a bus-path there instead ("5&2c2a1a5f&0&2") — skipped.
    $serial = $null
    if ($deviceId) {
        $segments = $deviceId.Split([char]0x5C)   # ids are backslash-separated
        $tail = $segments[-1]
        if ($segments.Count -ge 2 -and $segments[1] -match '\+([^+]+)$') {
            $serial = $matches[1]
        } elseif ($tail -and $tail -notmatch '&' -and $tail.Length -ge 4 -and $tail -notmatch '^\d+$') {
            $serial = $tail
        }
    }

    [pscustomobject]@{
        Port         = $port
        Description  = if ($device) { $device.Name } else { '(no device record)' }
        Manufacturer = if ($device) { $device.Manufacturer } else { $null }
        SerialNumber = $serial
        DeviceId     = $deviceId
    }
}
