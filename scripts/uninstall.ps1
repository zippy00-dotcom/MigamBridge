param(
    [string]$TaskName = "MigamBridgeTray"
)

Get-Process -Name "MigamBridgeTray" -ErrorAction SilentlyContinue | Stop-Process -Force

if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    Write-Host "제거 완료: 작업 스케줄러 '$TaskName' 등록 해제됨."
} else {
    Write-Host "등록된 작업이 없음: '$TaskName'"
}
