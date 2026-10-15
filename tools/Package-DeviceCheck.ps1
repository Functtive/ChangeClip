param()
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_HOME = Join-Path $taskRoot '.tools\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $taskRoot '.tools\nuget'
& (Join-Path $taskRoot '.tools\dotnet\dotnet.exe') build (Join-Path $taskRoot 'src\ChangeClip.Service\ChangeClip.Service.csproj') -c Release -p:RequireHardware=true --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build alata za provjeru nije uspio.' }
[xml]$taskProps = Get-Content -LiteralPath (Join-Path $taskRoot 'Directory.Build.props') -Raw
$taskVersion = [string]($taskProps.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if ($taskVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Neispravna verzija.' }
$taskPackage = Join-Path $taskRoot ('artifacts\provjera-' + $taskVersion)
if (Test-Path -LiteralPath $taskPackage) { throw 'Paket već postoji; sačuvajte ga prije novog pakovanja.' }
$taskSource = Join-Path $taskRoot 'src\ChangeClip.Service\bin\Hardware\Release\net48'
New-Item -ItemType Directory -Path $taskPackage | Out-Null
Copy-Item -LiteralPath (Join-Path $taskSource 'ChangeClip.Service.exe') -Destination (Join-Path $taskPackage 'ChangeClip.Provjera.exe')
Copy-Item -LiteralPath (Join-Path $taskSource 'ChangeClip.Service.exe.config') -Destination (Join-Path $taskPackage 'ChangeClip.Provjera.exe.config')
foreach ($taskName in @('ChangeClip.Contracts.dll','ChangeClip.Backend.dll','ChangeClip.Devices.dll','ITLlib.dll')) {
    Copy-Item -LiteralPath (Join-Path $taskSource $taskName) -Destination $taskPackage
}
if ((Get-FileHash -LiteralPath (Join-Path $taskPackage 'ITLlib.dll') -Algorithm SHA256).Hash -ne 'B96A636F699AECFA5CC877EBBCE246C35C445AA423D2AD6FF1D91F8B95B05C4A') { throw 'Pogrešan ITL runtime.' }
foreach ($taskName in @('PROVJERA_UREDJAJA.md','USB_DRIVERS.md')) {
    Copy-Item -LiteralPath (Join-Path $taskRoot ('docs\' + $taskName)) -Destination $taskPackage
}
New-Item -ItemType Directory -Path (Join-Path $taskPackage 'tools') | Out-Null
foreach ($taskName in @('Inspect-UsbDevices.ps1','Install-UsbDrivers.ps1','Verify-Package.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $taskName) -Destination (Join-Path $taskPackage 'tools')
}
$taskCapabilityText = & (Join-Path $PSScriptRoot 'Verify-Package.ps1') -PackageRoot $taskPackage -RuntimeOnly -ExecutableName 'ChangeClip.Provjera.exe'
$taskCapability = $taskCapabilityText | ConvertFrom-Json
if (!$taskCapability.nv9Runtime -or $taskCapability.simulatorCompiled -or $taskCapability.version -ne ($taskVersion + '.0')) { throw 'Neispravan runtime alata za provjeru.' }
$taskFiles = @(Get-ChildItem -LiteralPath $taskPackage -File -Recurse | Sort-Object FullName | ForEach-Object {
    @{ path=$_.FullName.Substring($taskPackage.Length+1).Replace('\','/'); bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
@{ version=$taskVersion; executableName='ChangeClip.Provjera.exe'; purpose='DeviceCheck'; builtUtc=[DateTime]::UtcNow.ToString('o'); runtime=$taskCapability; files=$taskFiles } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $taskPackage 'manifest.json') -Encoding utf8
& (Join-Path $PSScriptRoot 'Verify-Package.ps1') -PackageRoot $taskPackage
$taskZip = Join-Path $taskRoot ('artifacts\ChangeClip-Provjera-' + $taskVersion + '.zip')
Compress-Archive -Path (Join-Path $taskPackage '*') -DestinationPath $taskZip -Force
Get-Item -LiteralPath $taskZip | Select-Object Name,Length
"Provjera uređaja: $taskPackage"
