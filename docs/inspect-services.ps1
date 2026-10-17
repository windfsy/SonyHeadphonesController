param(
    # 12-hex-digit MAC of the device to inspect, e.g. ./inspect-services.ps1 -Mac AABBCCDDEEFF
    [Parameter(Mandatory = $true)]
    [string]$Mac
)

$ErrorActionPreference = 'Continue'
$bts = Get-PnpDevice -Class Bluetooth | Where-Object { $_.InstanceId -like "*$Mac*" }
foreach ($d in $bts) {
    Write-Host ""
    Write-Host "=== $($d.FriendlyName) ==="
    Write-Host "InstanceId: $($d.InstanceId)"
    $props = Get-PnpDeviceProperty -InstanceId $d.InstanceId
    $props | Where-Object { $_.KeyName -like '*Service*' -or $_.KeyName -like '*UUID*' -or $_.KeyName -like '*SDP*' -or $_.KeyName -like '*Class*' } | ForEach-Object {
        Write-Host "  $($_.KeyName): $($_.Data)"
    }
}
