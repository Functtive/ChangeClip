param([switch]$WithInstaller)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskTools = Join-Path $taskRoot '.tools'
New-Item -ItemType Directory -Path $taskTools -Force | Out-Null
$taskDotnet = Join-Path $taskTools 'dotnet'
if (-not (Test-Path -LiteralPath (Join-Path $taskDotnet 'dotnet.exe'))) {
    $taskVersion = '8.0.426'
    $taskMetadata = Invoke-RestMethod 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json'
    $taskSdk = $taskMetadata.releases | ForEach-Object { $_.sdk; $_.sdks } | Where-Object { $_.version -eq $taskVersion } | Select-Object -First 1
    $taskFile = $taskSdk.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -like '*.zip' } | Select-Object -First 1
    if (-not $taskFile) { throw 'Službeni .NET SDK zapis nije pronađen.' }
    $taskZip = Join-Path $taskTools "dotnet-sdk-$taskVersion.zip"
    Write-Output "Preuzimam .NET SDK $taskVersion u projektni .tools folder."
    Invoke-WebRequest $taskFile.url -OutFile $taskZip
    if ((Get-FileHash -LiteralPath $taskZip -Algorithm SHA512).Hash -ne $taskFile.hash) { throw 'SDK SHA512 se ne poklapa.' }
    Expand-Archive -LiteralPath $taskZip -DestinationPath $taskDotnet -Force
}
& (Join-Path $taskDotnet 'dotnet.exe') --version
if ($LASTEXITCODE -ne 0) { throw 'SDK nije moguće pokrenuti.' }
if ($WithInstaller -and -not (Test-Path -LiteralPath (Join-Path $taskTools 'inno\ISCC.exe'))) {
    $taskSetup = Join-Path $taskTools 'innosetup-6.7.3.exe'
    Invoke-WebRequest 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe' -OutFile $taskSetup
    $taskSignature = Get-AuthenticodeSignature -LiteralPath $taskSetup
    if ($taskSignature.Status -ne 'Valid' -or $taskSignature.SignerCertificate.Subject -notmatch 'Pyrsys') { throw 'Inno Setup potpis nije potvrđen.' }
    $taskInnoDirectory = Join-Path $taskTools 'inno'
    $taskProcess = Start-Process -FilePath $taskSetup -ArgumentList @('/CURRENTUSER', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOICONS', '/TASKS=', ('/DIR="' + $taskInnoDirectory + '"')) -WindowStyle Hidden -PassThru -Wait
    if ($taskProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $taskInnoDirectory 'ISCC.exe'))) { throw 'Inno Setup priprema nije uspjela.' }
}
