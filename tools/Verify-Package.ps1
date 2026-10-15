param([string]$PackageRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath($PackageRoot).TrimEnd('\')
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
$taskCapabilityText = & (Join-Path $taskRoot 'ChangeClip.Service.exe') capabilities
if ($LASTEXITCODE -ne 0) { throw 'Runtime provjera nije izvršena.' }
$taskCapability = $taskCapabilityText | ConvertFrom-Json
if (!$taskCapability.nv9Runtime -or $taskCapability.simulatorCompiled -or $taskCapability.version -ne ($taskManifest.version + '.0')) { throw 'Pogrešan hardware runtime.' }
"PASS: integritet paketa $($taskManifest.version), NV9 runtime prisutan, simulator nije ugrađen."
