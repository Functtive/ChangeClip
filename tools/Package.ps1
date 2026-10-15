param([string]$Version = '0.2.0')
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version mora imati oblik 0.2.0.' }
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskOutput = Join-Path $taskRoot 'src\ChangeClip.Frontend\bin\Release\net48'
$taskPortable = Join-Path $taskRoot ('artifacts\portable-' + $Version)
$taskCompiler = Join-Path $taskRoot '.tools\inno\ISCC.exe'
if (-not (Test-Path -LiteralPath (Join-Path $taskOutput 'ChangeClip.exe'))) { throw 'Prvo pokrenite tools\Build.ps1.' }
New-Item -ItemType Directory -Path $taskPortable -Force | Out-Null
Copy-Item -Path (Join-Path $taskOutput '*.exe'), (Join-Path $taskOutput '*.dll'), (Join-Path $taskOutput '*.config') -Destination $taskPortable -Force
Copy-Item -LiteralPath (Join-Path $taskOutput 'config') -Destination $taskPortable -Recurse -Force
New-Item -ItemType Directory -Path (Join-Path $taskPortable 'tools') -Force | Out-Null
foreach ($taskName in @('Inspect-UsbDevices.ps1','Install-UsbDrivers.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $taskName) -Destination (Join-Path $taskPortable 'tools') -Force
}
foreach ($taskName in @('INSTALACIJA.md','USB_DRIVERS.md')) {
    Copy-Item -LiteralPath (Join-Path $taskRoot "docs\$taskName") -Destination $taskPortable -Force
}
Compress-Archive -Path (Join-Path $taskPortable '*') -DestinationPath (Join-Path $taskRoot ('artifacts\ChangeClip-Portable-' + $Version + '.zip')) -Force
if (-not (Test-Path -LiteralPath $taskCompiler)) { throw 'Pripremite installer compiler: tools\Prepare-BuildTools.ps1 -WithInstaller.' }
& $taskCompiler ('/DPublishDir=' + $taskPortable) ('/DAppVersion=' + $Version) (Join-Path $taskRoot 'installer\ChangeClip.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer nije kompajliran.' }
Get-ChildItem -LiteralPath (Join-Path $taskRoot 'artifacts') -File | Select-Object Name,Length
