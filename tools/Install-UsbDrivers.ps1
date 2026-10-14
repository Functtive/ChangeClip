# Run on the TARGET PC only after reviewing Inspect-UsbDevices output.
# Default is validation-only. -Install explicitly stages and installs the exact signed INF.
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory = $true)][string]$DriverRoot,
    [Parameter(Mandatory = $true)][string]$DeviceInstanceId,
    [switch]$Install
)
$ErrorActionPreference = 'Stop'
if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { throw 'ARM64 driver package has not been verified.' }
$device = Get-PnpDevice -PresentOnly -InstanceId $DeviceInstanceId -ErrorAction Stop
$hardwareIds = @((Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName DEVPKEY_Device_HardwareIds -ErrorAction Stop).Data)
$is64 = [Environment]::Is64BitOperatingSystem
$relative = $null
if ($hardwareIds -contains 'USB\VID_191C&PID_4104') {
    $relative = if ($is64) { 'itlusbdevice_x64_Signed\itlusbdevice_x64.inf' } else { 'itlusbdevice_x86_Signed\itlusbdevice_x86.inf' }
} elseif ($is64 -and $hardwareIds -contains 'USB\VID_0403&PID_F248') {
    $relative = 'DA2 - 64 Bit - Driver\ftdibus.inf'
} elseif ($is64 -and $hardwareIds -contains 'FTDIBUS\COMPORT&VID_0403&PID_F248') {
    $relative = 'DA2 - 64 Bit - Driver\ftdiport.inf'
}
if (!$relative) { throw 'No reviewed driver matches this exact present hardware ID. Unknown USB-ccTalk dongles require their manufacturer driver.' }
$hashes = @{
 'itlusbdevice_x64_Signed\itlusbdevice_x64.inf' = '8DCE00B144C88D36B6646D2AA62B3180E754A574BF6BCC7B1E00EB5D74AE6ABD'
 'itlusbdevice_x64_Signed\itlusbdevice_x64.cat' = 'E52E82E745A71CEE35721226D8F33AFED9AAA901032FC5E38F201391DEDDC0A5'
 'itlusbdevice_x86_Signed\itlusbdevice_x86.inf' = '6A3B0EA1DAA256D0CC38098D04EF51C1B5A72C272AE2B53E9BF063F2D7B27327'
 'itlusbdevice_x86_Signed\itlusbdevice_x86.cat' = 'B8F1BC6BC98502C3E08C46D82F5891AE06FD1D8ECA44C5CF632B9C26303A5D13'
 'DA2 - 64 Bit - Driver\ftdibus.inf' = '3C6A64B500A31006AB88CC91581BB3EAA66E0DEC3011BEB8C1741ECE164308F2'
 'DA2 - 64 Bit - Driver\ftdibus.cat' = 'B2508E8AB1ABC297DF0881F60C40AB495749E7F6C4C76D0DA4AA72CB071453C3'
 'DA2 - 64 Bit - Driver\ftdiport.inf' = '607D9A53ABA36C88C3CE0D259A7F441D0A36F811AE32E418680440F521A710DA'
 'DA2 - 64 Bit - Driver\ftdiport.cat' = '661CE147A903A951E217B177A9BA793E50EC1073E0660412B671E81D652E8131'
}
$inf = Join-Path $DriverRoot $relative
$catalogRelative = [IO.Path]::ChangeExtension($relative, '.cat')
$catalog = Join-Path $DriverRoot $catalogRelative
foreach ($item in @(@{Path = $inf; Relative = $relative}, @{Path = $catalog; Relative = $catalogRelative})) {
    if ((Get-FileHash -LiteralPath $item.Path -Algorithm SHA256).Hash -ne $hashes[$item.Relative]) { throw ('Reviewed driver hash mismatch: ' + $item.Path) }
}
$signature = Get-AuthenticodeSignature -LiteralPath $catalog
if ($signature.Status -ne 'Valid') { throw ('Catalog signature is not Valid: ' + $signature.Status) }
[pscustomobject]@{ Device = $device.FriendlyName; InstanceId = $device.InstanceId; HardwareIds = $hardwareIds; ExactInf = $inf; Catalog = $catalog; Signature = $signature.Status.ToString(); Signer = $signature.SignerCertificate.Subject; InstallRequested = [bool]$Install } | Format-List
if (!$Install) { Write-Output 'Validation complete. No driver has been installed. Review the exact match before using -Install.'; return }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Installation requires an elevated PowerShell window on the target PC.' }
if ($PSCmdlet.ShouldProcess($inf, 'Stage/install signed driver for matching present hardware IDs (pnputil verifies catalog membership)')) {
    & "$env:windir\System32\pnputil.exe" /add-driver $inf /install
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 3010) { throw ('pnputil failed: ' + $LASTEXITCODE) }
    Write-Output 'Run Inspect-UsbDevices again and verify the resulting COM port. No security policy has been changed.'
}
