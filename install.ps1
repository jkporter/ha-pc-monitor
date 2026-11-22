<#
.SYNOPSIS
  Installs HassHwStats and starts it at logon (default) or as a Windows service.

.DESCRIPTION
  Default: a scheduled task that runs HassHwStats elevated in your session when you log on.
  This is what you want on a gaming PC: it can read CPU/GPU sensors (needs admin) AND RTSS frame rates
  (which only exist in your desktop session).

  -AsService: a Windows service instead. Starts before logon, but FPS from RTSS will not work.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\install.ps1
  powershell -ExecutionPolicy Bypass -File .\install.ps1 -AsService
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "$env:ProgramFiles\HassHwStats",
    [switch]$AsService
)

$ErrorActionPreference = 'Stop'
$name = 'HassHwStats'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Re-launching elevated...'
    $argsList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-InstallDir', "`"$InstallDir`"")
    if ($AsService) { $argsList += '-AsService' }
    Start-Process powershell -Verb RunAs -ArgumentList $argsList -Wait
    exit
}

$source = $PSScriptRoot
$exe = Join-Path $InstallDir "$name.exe"

# Stop a running copy so the exe can be replaced.
Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue | Stop-ScheduledTask -ErrorAction SilentlyContinue
if (Get-Service $name -ErrorAction SilentlyContinue) { Stop-Service $name -ErrorAction SilentlyContinue }
Get-Process $name -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item (Join-Path $source "$name.exe") $InstallDir -Force
$config = Join-Path $InstallDir 'appsettings.json'
if (Test-Path $config) {
    Write-Host "Keeping existing $config"
} else {
    Copy-Item (Join-Path $source 'appsettings.json') $InstallDir
    Write-Host "Created $config. Edit it with your MQTT broker details." -ForegroundColor Yellow
}

if (-not (Get-Service PawnIO -ErrorAction SilentlyContinue)) {
    Write-Warning 'PawnIO driver not found. CPU temperatures and power need it: install from https://pawnio.eu (or: winget install namazso.PawnIO).'
}

if ($AsService) {
    Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
    if (-not (Get-Service $name -ErrorAction SilentlyContinue)) {
        New-Service -Name $name -BinaryPathName "`"$exe`"" -DisplayName 'Home Assistant hardware stats' `
            -Description 'Publishes CPU, GPU and WireView Pro II stats to Home Assistant over MQTT.' -StartupType Automatic | Out-Null
        sc.exe failure $name reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Null
    }
    Start-Service $name
    Write-Host "Installed and started the $name service." -ForegroundColor Green
} else {
    if (Get-Service $name -ErrorAction SilentlyContinue) {
        Stop-Service $name -ErrorAction SilentlyContinue
        sc.exe delete $name | Out-Null
    }
    $user = "$env:USERDOMAIN\$env:USERNAME"
    $action = New-ScheduledTaskAction -Execute $exe -WorkingDirectory $InstallDir
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable `
        -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew
    $taskPrincipal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
    Register-ScheduledTask -TaskName $name -Action $action -Trigger $trigger -Settings $settings -Principal $taskPrincipal `
        -Description 'Publishes CPU, GPU, FPS and WireView Pro II stats to Home Assistant over MQTT.' -Force | Out-Null
    Start-ScheduledTask -TaskName $name
    Write-Host "Installed. $name now starts when $user logs on, and is running now." -ForegroundColor Green
}

Write-Host "Log file: $(Join-Path $InstallDir "$name.log")"
