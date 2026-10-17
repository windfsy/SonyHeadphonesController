$ErrorActionPreference = 'Continue'
$bts = Get-PnpDevice -Class Bluetooth | Where-Object { $_.InstanceId -like '*AABBCCDDEEFF*' }
foreach ($d in $bts) {
    Write-Host ""
    Write-Host "=== $($d.FriendlyName) ==="
    Write-Host "InstanceId: $($d.InstanceId)"
    $props = Get-PnpDeviceProperty -InstanceId $d.InstanceId
    $props | Where-Object { $_.KeyName -like '*Service*' -or $_.KeyName -like '*UUID*' -or $_.KeyName -like '*SDP*' -or $_.KeyName -like '*Class*' } | ForEach-Object {
        Write-Host "  $($_.KeyName): $($_.Data)"
    }
}
