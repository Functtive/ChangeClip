[CmdletBinding()]
param([string]$DriverRoot)
$ErrorActionPreference = 'Stop'
Write-Output 'READ ONLY: USB/COM inventory; no driver installation or device commands.'
$devices = @(Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -match '^(USB|FTDIBUS)\\' -or $_.Class -eq 'Ports' })
$devices | ForEach-Object {
    $device = $_
    $ids = (Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName DEVPKEY_Device_HardwareIds -ErrorAction SilentlyContinue).Data
    $driver = (Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName DEVPKEY_Device_DriverInfPath -ErrorAction SilentlyContinue).Data
    [pscustomobject]@{ Name = $device.FriendlyName; InstanceId = $device.InstanceId; Status = $device.Status; HardwareIds = @($ids); DriverInf = $driver }
} | ConvertTo-Json -Depth 4
Write-Output ('COM ports: ' + ([System.IO.Ports.SerialPort]::GetPortNames() -join ', '))
if ($DriverRoot) {
    Get-ChildItem -LiteralPath $DriverRoot -Recurse -Filter '*.inf' | ForEach-Object {
        $inf = $_
        $text = Get-Content -LiteralPath $inf.FullName
        $catalogLine = @($text | Where-Object { $_ -match '^\s*CatalogFile\s*=' }) | Select-Object -First 1
        $catalogName = if ($catalogLine) { ($catalogLine -split '=',2)[1].Trim() } else { $null }
        $catalog = if ($catalogName) { Join-Path $inf.DirectoryName $catalogName } else { $null }
        $signature = if ($catalog -and (Test-Path -LiteralPath $catalog)) { Get-AuthenticodeSignature -LiteralPath $catalog } else { $null }
        [pscustomobject]@{
            Inf = $inf.FullName; InfSHA256 = (Get-FileHash -LiteralPath $inf.FullName -Algorithm SHA256).Hash
            DriverVersion = @($text | Where-Object { $_ -match '^\s*DriverVer\s*=' })
            ModelLines = @($text | Where-Object { $_ -notmatch '^\s*;' -and $_ -match '=.*,\s*(USB|FTDIBUS)\\' })
            Catalog = $catalog; CatalogSignature = if ($signature) { $signature.Status.ToString() } else { 'Missing' }
            Signer = if ($signature) { $signature.SignerCertificate.Subject } else { $null }
            CatalogSHA256 = if ($catalog -and (Test-Path -LiteralPath $catalog)) { (Get-FileHash -LiteralPath $catalog -Algorithm SHA256).Hash } else { $null }
        }
    } | ConvertTo-Json -Depth 4
}
