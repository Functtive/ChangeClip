param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskDotnet = Join-Path $taskRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $taskDotnet)) { throw 'Prvo pokrenite tools\Prepare-BuildTools.ps1.' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_HOME = Join-Path $taskRoot '.tools\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $taskRoot '.tools\nuget'
& $taskDotnet build (Join-Path $taskRoot 'ChangeClip.sln') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build nije uspio.' }
if (-not $SkipTests) {
    foreach ($taskTest in @('ChangeClip.Backend.Tests', 'ChangeClip.Devices.Tests', 'ChangeClip.Integration.Tests', 'ChangeClip.Frontend.Tests')) {
        & (Join-Path $taskRoot "tests\$taskTest\bin\Release\net48\$taskTest.exe")
        if ($LASTEXITCODE -ne 0) { throw "Testovi nisu prošli: $taskTest" }
    }
}
