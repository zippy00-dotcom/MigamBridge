<#
.SYNOPSIS
  MigamBridgeTray.exe를 "로그온 시 자동 실행"으로 등록한다.

.설명
  진짜 Windows 서비스(SCM)는 화면(UI)을 못 띄우기 때문에 트레이 아이콘을 쓸 수 없다.
  대신 Windows 작업 스케줄러(Task Scheduler)에 "로그온 시 트리거"로 등록해서
  사용자가 로그인하면 자동으로 트레이 앱이 뜨도록 한다 (서비스처럼 항상 상주하는 효과는 동일).

.사용법
  publish 폴더(EXE가 있는 곳)에서 관리자 권한 PowerShell로 실행:
    .\install.ps1
  또는 EXE 경로를 직접 지정:
    .\install.ps1 -ExePath "C:\Program Files\Migam\MigamBridgeTray.exe"
#>

param(
    [string]$ExePath = (Join-Path $PSScriptRoot "MigamBridgeTray.exe"),
    [string]$TaskName = "MigamBridgeTray"
)

if (-not (Test-Path $ExePath)) {
    Write-Error "실행 파일을 찾을 수 없음: $ExePath  (-ExePath로 정확한 경로를 지정해줘)"
    exit 1
}

$action    = New-ScheduledTaskAction -Execute $ExePath
$trigger   = New-ScheduledTaskTrigger -AtLogOn
$settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null

Write-Host "등록 완료: 작업 스케줄러 '$TaskName' - 다음 로그온부터 자동 실행됩니다."
Write-Host "지금 바로 실행하려면: Start-ScheduledTask -TaskName '$TaskName'"
