; ============================================================================
; MigamBridge.iss — Migam 측색기 브리지 설치 프로그램 (Inno Setup 스크립트)
;
; [사전 준비]
;   1. Inno Setup 설치: https://jrsoftware.org/isdl.php (무료)
;   2. 아래 명령으로 self-contained 단일 exe를 먼저 만들어둘 것
;      (MigamBridge.Tray 폴더에서 실행):
;
;      dotnet publish -c Release -r win-x64 --self-contained true ^
;        -p:PublishSingleFile=true -o .\publish
;
;   3. 이 파일(MigamBridge.iss)을 Inno Setup으로 열고 컴파일(Ctrl+F9)
;      → installer\Output\MigamBridgeSetup.exe 생성됨
;
; [이 스크립트가 하는 일]
;   - publish 폴더 전체(exe, eXact.dll, appsettings.json 등)를
;     "C:\Program Files\Migam\MigamBridgeTray"에 설치
;   - 로그인 시 자동 실행되도록 "시작프로그램" 바로가기 생성
;     (Windows 서비스가 아니라 트레이 상주 앱이라 이 방식이 맞음 - README_TRAY.md 참고)
;   - 설치 완료 후 바로 트레이 앱 실행(선택 가능)
;   - 제거 시 실행 중인 프로세스를 먼저 종료
; ============================================================================

#define MyAppName "MigamBridge"
#define MyAppVersion "1.0.4.2"
#define MyAppPublisher "Migam"
#define MyAppExeName "MigamBridgeTray.exe"
; publish 폴더 경로 - 이 .iss 파일 기준 상대경로. 실제 폴더 위치에 맞게 수정할 것.
#define PublishDir "..\MigamBridge.Tray\publish"

[Setup]
AppId={{9B2B9F1E-6C3A-4E7D-9C2A-5F1B7C4A2D3E}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\Migam\MigamBridgeTray
DefaultGroupName=Migam
DisableProgramGroupPage=yes
; Program Files에 설치하므로 설치 자체는 관리자 권한 필요.
; 단, 설치 후 실행되는 트레이 앱 자체는 일반 사용자 권한으로 돈다(아래 [Run]의 runasoriginaluser).
PrivilegesRequired=admin
OutputDir=Output
OutputBaseFilename=MigamBridgeSetup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
ArchitecturesInstallIn64BitMode=x64
; 자동 업데이트(UpdateChecker.cs)가 이 Setup.exe를 /VERYSILENT로 재실행할 때,
; 지금 떠있는 MigamBridgeTray.exe를 Inno Setup이 알아서 감지하고 종료시킨 뒤 덮어쓰도록 함.
; Program.cs의 뮤텍스 이름과 반드시 일치해야 함.
AppMutex=MigamBridgeTray_SingleInstance_Mutex

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Files]
; publish 폴더의 모든 파일(exe, dll, appsettings.json, eXact.dll 등)을 통째로 설치 폴더에 복사
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; 시작 메뉴 바로가기 (선택)
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
; 로그인 시 자동 실행 - "시작프로그램" 폴더에 바로가기 생성
; (실제 서비스 등록이 아니라 트레이 상주 앱이 로그온 시 뜨게 하는 표준적인 방법)
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Run]
; 설치 완료 직후 바로 실행. runasoriginaluser로 관리자 권한이 아닌
; 일반 사용자 권한으로 실행되도록 함(트레이 앱을 굳이 관리자로 띄울 필요 없음).
; ⚠️ skipifsilent를 일부러 안 씀: 자동 업데이트(UpdateChecker)가 /VERYSILENT로 무음 설치를
;    실행하는데, skipifsilent가 있으면 그 경우 재실행이 통째로 스킵되어 사용자가 직접 다시
;    켜야 하는 문제가 있었음. 수동 설치든 자동 업데이트든 항상 설치 후 바로 실행되게 함.
Filename: "{app}\{#MyAppExeName}"; Description: "설치 후 바로 실행"; Flags: nowait postinstall runasoriginaluser

[UninstallRun]
; 제거 전에 실행 중인 트레이 앱을 먼저 종료 (안 그러면 exe/dll이 사용 중이라 삭제 실패할 수 있음)
Filename: "{cmd}"; Parameters: "/C taskkill /IM {#MyAppExeName} /F"; Flags: runhidden; RunOnceId: "KillMigamBridgeTray"
