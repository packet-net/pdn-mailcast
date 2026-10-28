# install-service.ps1: runs pdn-mailcast-receiver as a Windows service, started at boot and
# started again a minute after it stops with a failure, as systemd does on Linux.
#
# Run it from an administrator PowerShell, in the folder the zip was unzipped to:
#   powershell -ExecutionPolicy Bypass -File .\install-service.ps1
# Running it again puts the service right (after moving the folder, say). To take it away:
#   sc.exe stop pdn-mailcast-receiver; sc.exe delete pdn-mailcast-receiver

$ErrorActionPreference = 'Stop'
$name = 'pdn-mailcast-receiver'
$exe = Join-Path $PSScriptRoot 'pdn-mailcast-receiver.exe'
$folder = Join-Path $env:ProgramData 'pdn-mailcast'
$config = Join-Path $folder 'receiver.json'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this from an administrator PowerShell (right-click PowerShell, Run as administrator).'
}
if (-not (Test-Path $exe)) {
    throw "$exe is not here: run this script from the folder the zip was unzipped to."
}

# The config, from the example the first time only: the status page saves settings into it.
New-Item -ItemType Directory -Force $folder | Out-Null
if (-not (Test-Path $config)) {
    Copy-Item (Join-Path $PSScriptRoot 'receiver.example.json') $config
    Write-Host "Made $config from the example: set your BBS in it, or on the status page."
}

$binPath = "`"$exe`" --service"
if (Get-Service $name -ErrorAction SilentlyContinue) {
    sc.exe stop $name | Out-Null
    Start-Sleep -Seconds 3
    sc.exe config $name binPath= $binPath start= delayed-auto | Out-Null
} else {
    sc.exe create $name binPath= $binPath start= delayed-auto DisplayName= 'pdn-mailcast receiver' | Out-Null
}
if ($LASTEXITCODE -ne 0) { throw "sc.exe could not set up the service ($LASTEXITCODE)" }
sc.exe description $name 'Hears GB7RDG''s bulletins on 40 m and hands each one to your BBS.' | Out-Null
# Start it again a minute after any failure, and count a stop with an error as a failure too
# (that is how the receiver says it needs restarting); the count resets after a day.
sc.exe failure $name reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
sc.exe failureflag $name 1 | Out-Null
sc.exe start $name | Out-Null

Write-Host "The service $name is running. Its log is $(Join-Path $folder 'receiver.log');"
Write-Host 'the status page is http://localhost:8130/, or port 8130 at this PC''s address, unless receiver.json says another port.'
