param([switch]$SkipTests, [switch]$HardwareOnly)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskDotnet = Join-Path $taskRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $taskDotnet)) { throw 'Prvo pokrenite tools\Prepare-BuildTools.ps1.' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_HOME = Join-Path $taskRoot '.tools\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $taskRoot '.tools\nuget'
if (-not $HardwareOnly) {
    & $taskDotnet build (Join-Path $taskRoot 'ChangeClip.sln') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Razvojni build nije uspio.' }
    if (-not $SkipTests) {
        foreach ($taskTest in @('ChangeClip.Backend.Tests','ChangeClip.Devices.Tests','ChangeClip.Integration.Tests','ChangeClip.Frontend.Tests','ChangeClip.Service.Tests')) {
            & (Join-Path $taskRoot "tests\$taskTest\bin\Release\net48\$taskTest.exe")
            if ($LASTEXITCODE -ne 0) { throw "Provjere nisu prošle: $taskTest" }
        }
    }
}
foreach ($taskProject in @('tests\ChangeClip.Frontend.Tests\ChangeClip.Frontend.Tests.csproj','src\ChangeClip.Service\ChangeClip.Service.csproj')) {
    & $taskDotnet build (Join-Path $taskRoot $taskProject) -c Release -p:RequireHardware=true --nologo
    if ($LASTEXITCODE -ne 0) { throw "Hardware build nije uspio: $taskProject" }
}
if (-not $SkipTests) {
    & (Join-Path $taskRoot 'tests\ChangeClip.Frontend.Tests\bin\Hardware\Release\net48\ChangeClip.Frontend.Tests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Provjere korisničkog hardware interfejsa nisu prošle.' }
}
$taskCapabilityText = & (Join-Path $taskRoot 'src\ChangeClip.Service\bin\Hardware\Release\net48\ChangeClip.Service.exe') capabilities
if ($LASTEXITCODE -ne 0) { throw 'Hardware runtime provjera nije prošla.' }
$taskCapability = $taskCapabilityText | ConvertFrom-Json
if (!$taskCapability.nv9Runtime -or $taskCapability.simulatorCompiled) { throw 'Korisnički build nema ispravan hardware adapter.' }
$taskCapabilityText
'Build gotov. Fizički uređaji nisu pokretani.'
