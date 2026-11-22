<#
.SYNOPSIS
  Removes HassHwStats (scheduled task or service). Add -RemoveFiles to delete the install folder too.
  Entities stay in Home Assistant until you delete the devices there (Settings > Devices > MQTT).
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "$env:ProgramFiles\HassHwStats",
    [switch]$RemoveFiles
)
$ErrorActionPreference = 'Stop'
$name = 'HassHwStats'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $argsList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-InstallDir', "`"$InstallDir`"")
    if ($RemoveFiles) { $argsList += '-RemoveFiles' }
    Start-Process powershell -Verb RunAs -ArgumentList $argsList -Wait
    exit
}

if (Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue) {
    Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $name -Confirm:$false
}
if (Get-Service $name -ErrorAction SilentlyContinue) {
    Stop-Service $name -ErrorAction SilentlyContinue
    sc.exe delete $name | Out-Null
}
Get-Process $name -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
if ($RemoveFiles -and (Test-Path $InstallDir)) { Remove-Item $InstallDir -Recurse -Force }
Write-Host "$name removed." -ForegroundColor Green
