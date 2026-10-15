param([string]$PackageRoot = (Split-Path -Parent $PSScriptRoot), [switch]$RuntimeOnly)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath($PackageRoot).TrimEnd('\')
function Read-ChangeClipRuntime {
    $taskInfo = [Diagnostics.ProcessStartInfo]::new((Join-Path $taskRoot 'ChangeClip.Service.exe'), 'capabilities')
    $taskInfo.UseShellExecute = $false
    $taskInfo.RedirectStandardOutput = $true
    $taskInfo.RedirectStandardError = $true
    $taskInfo.StandardOutputEncoding = [Text.Encoding]::UTF8
    $taskInfo.StandardErrorEncoding = [Text.Encoding]::UTF8
    $taskInfo.CreateNoWindow = $true
    $taskProcess = [Diagnostics.Process]::Start($taskInfo)
    try {
        $taskOut = $taskProcess.StandardOutput.ReadToEndAsync()
        $taskError = $taskProcess.StandardError.ReadToEndAsync()
        if (!$taskProcess.WaitForExit(10000)) { $taskProcess.Kill(); throw 'Runtime provjera nije završila.' }
        $taskText = $taskOut.GetAwaiter().GetResult()
        $taskDetails = $taskError.GetAwaiter().GetResult()
        if ($taskProcess.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($taskText)) { throw ('Runtime provjera nije uspjela. ' + $taskDetails) }
        return $taskText.Trim()
    } finally { $taskProcess.Dispose() }
}
if ($RuntimeOnly) { Read-ChangeClipRuntime; return }
$taskManifest = Get-Content -LiteralPath (Join-Path $taskRoot 'manifest.json') -Raw | ConvertFrom-Json
$taskExpected = @('manifest.json')
foreach ($taskFile in $taskManifest.files) {
    $taskPath = [IO.Path]::GetFullPath((Join-Path $taskRoot $taskFile.path))
    if (!$taskPath.StartsWith($taskRoot + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Manifest sadrži putanju izvan paketa.' }
    if (!(Test-Path -LiteralPath $taskPath -PathType Leaf) -or (Get-FileHash -LiteralPath $taskPath -Algorithm SHA256).Hash -ne $taskFile.sha256) { throw "Neispravan ili nedostajući fajl: $($taskFile.path)" }
    $taskExpected += $taskFile.path.Replace('\','/')
}
$taskActual = @(Get-ChildItem -LiteralPath $taskRoot -File -Recurse | ForEach-Object { $_.FullName.Substring($taskRoot.Length+1).Replace('\','/') })
if (@(Compare-Object ($taskExpected | Sort-Object) ($taskActual | Sort-Object)).Count -ne 0) { throw 'Paket ima neočekivane ili nedostajuće fajlove.' }
$taskCapabilityText = Read-ChangeClipRuntime
$taskCapability = $taskCapabilityText | ConvertFrom-Json
if (!$taskCapability.nv9Runtime -or $taskCapability.simulatorCompiled -or $taskCapability.version -ne ($taskManifest.version + '.0')) { throw 'Pogrešan hardware runtime.' }
"PASS: integritet paketa $($taskManifest.version), NV9 runtime prisutan, simulator nije ugrađen."
