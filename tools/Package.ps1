param()
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
[xml]$taskProps = Get-Content -LiteralPath (Join-Path $taskRoot 'Directory.Build.props') -Raw
$taskVersion = [string]($taskProps.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if ($taskVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Neispravna jedinstvena verzija u Directory.Build.props.' }
& (Join-Path $PSScriptRoot 'Build.ps1') -HardwareOnly -SkipTests
$taskFrontend = Join-Path $taskRoot 'src\ChangeClip.Frontend\bin\Hardware\Release\net48'
$taskService = Join-Path $taskRoot 'src\ChangeClip.Service\bin\Hardware\Release\net48'
$taskCompiler = Join-Path $taskRoot '.tools\inno\ISCC.exe'
if (-not (Test-Path -LiteralPath $taskCompiler)) { throw 'Pripremite installer compiler: tools\Prepare-BuildTools.ps1 -WithInstaller.' }
$taskPortable = Join-Path $taskRoot ('artifacts\portable-' + $taskVersion)
if (Test-Path -LiteralPath $taskPortable) { $taskPortable += '-' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,6) }
New-Item -ItemType Directory -Path $taskPortable | Out-Null
foreach ($taskName in @('ChangeClip.exe','ChangeClip.exe.config','ChangeClip.Contracts.dll','ChangeClip.Backend.dll','ChangeClip.Devices.dll','ITLlib.dll')) {
    $taskSource = Join-Path $taskFrontend $taskName
    if (-not (Test-Path -LiteralPath $taskSource -PathType Leaf)) { throw "Nedostaje obavezan runtime fajl: $taskName" }
    Copy-Item -LiteralPath $taskSource -Destination $taskPortable
}
foreach ($taskName in @('ChangeClip.Service.exe','ChangeClip.Service.exe.config')) {
    Copy-Item -LiteralPath (Join-Path $taskService $taskName) -Destination $taskPortable
}
foreach ($taskName in @('ChangeClip.Contracts.dll','ChangeClip.Backend.dll','ChangeClip.Devices.dll','ITLlib.dll')) {
    if ((Get-FileHash -LiteralPath (Join-Path $taskFrontend $taskName)).Hash -ne (Get-FileHash -LiteralPath (Join-Path $taskService $taskName)).Hash) { throw "Frontend i servis imaju različit runtime: $taskName" }
}
$taskVendorHash = (Get-FileHash -LiteralPath (Join-Path $taskPortable 'ITLlib.dll') -Algorithm SHA256).Hash
if ($taskVendorHash -ne 'B96A636F699AECFA5CC877EBBCE246C35C445AA423D2AD6FF1D91F8B95B05C4A') { throw 'ITL runtime nije pregledana lokalna verzija.' }
New-Item -ItemType Directory -Path (Join-Path $taskPortable 'config'),(Join-Path $taskPortable 'tools') | Out-Null
Copy-Item -LiteralPath (Join-Path $taskRoot 'config\device.hardware.example.json') -Destination (Join-Path $taskPortable 'config')
foreach ($taskName in @('Inspect-UsbDevices.ps1','Install-UsbDrivers.ps1','Verify-Package.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $taskName) -Destination (Join-Path $taskPortable 'tools')
}
foreach ($taskName in @('INSTALACIJA.md','USB_DRIVERS.md','SERVIS.md','HARDWARE_TEST.md')) {
    Copy-Item -LiteralPath (Join-Path $taskRoot "docs\$taskName") -Destination $taskPortable
}
$taskCapabilityText = & (Join-Path $taskPortable 'ChangeClip.Service.exe') capabilities
if ($LASTEXITCODE -ne 0) { throw 'Zapakovani runtime nije izvršiv.' }
$taskCapability = $taskCapabilityText | ConvertFrom-Json
if (!$taskCapability.nv9Runtime -or $taskCapability.simulatorCompiled -or $taskCapability.version -ne ($taskVersion + '.0')) { throw 'Zapakovano izdanje nije produkcijski hardware build očekivane verzije.' }
$taskFiles = @(Get-ChildItem -LiteralPath $taskPortable -File -Recurse | Sort-Object FullName | ForEach-Object {
    @{ path = $_.FullName.Substring($taskPortable.Length + 1).Replace('\','/'); bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
@{ version=$taskVersion; builtUtc=[DateTime]::UtcNow.ToString('o'); runtime=$taskCapability; files=$taskFiles } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $taskPortable 'manifest.json') -Encoding utf8
& (Join-Path $PSScriptRoot 'Verify-Package.ps1') -PackageRoot $taskPortable
Compress-Archive -Path (Join-Path $taskPortable '*') -DestinationPath (Join-Path $taskRoot ('artifacts\ChangeClip-Portable-' + $taskVersion + '.zip')) -Force
& $taskCompiler ('/DPublishDir=' + $taskPortable) ('/DAppVersion=' + $taskVersion) (Join-Path $taskRoot 'installer\ChangeClip.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer nije kompajliran.' }
foreach ($taskName in @(('ChangeClip-Setup-' + $taskVersion + '.exe'),('ChangeClip-Portable-' + $taskVersion + '.zip'))) {
    Get-Item -LiteralPath (Join-Path $taskRoot ('artifacts\' + $taskName)) | Select-Object Name,Length
}
"Portable folder: $taskPortable"
