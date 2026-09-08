# MigamBridge.Tray — 트레이 상주 앱

`MigamBridge`(API)를 그대로 인프로세스로 띄우면서, 우측 하단 트레이 아이콘 UI를 추가한 배포용 앱.

## 왜 "Windows 서비스"가 아니라 "트레이 상주 앱"인가

진짜 Windows 서비스(SCM에 등록되는 서비스)는 Session 0라는 별도 세션에서 돌기 때문에
화면에 아무 것도 못 띄운다(트레이 아이콘 포함, Vista 이후 정책). 그래서 "설치하면 트레이에 상주하고
클릭하면 UI가 뜨는" 요구사항은 서비스가 아니라 **로그온 시 자동 실행되는 일반 프로그램**으로 구현했다.
동작 결과(항상 백그라운드에 떠있음)는 서비스와 동일하다.

## 구조

```
MigamBridge/                 API 서버 (기존 그대로, 재사용을 위해 BridgeHost/DeviceRouter로 분리)
  BridgeHost.cs               WebApplication 구성 - Program.cs와 Tray 양쪽에서 재사용
  DeviceRouter.cs             벤더당 1대 연결 제한 강제 + 연결상태 이벤트 전파
  Drivers/NixDriver.cs        (수정) DeviceDiscovered/ConnectionChanged 이벤트, QuickConnectAsync 추가
  Drivers/XriteDriver.cs      (수정) 위와 동일한 이벤트 추가
  Program.cs                  독립 실행형 진입점 (개발/디버그용, 그대로 둠)

MigamBridge.Tray/            신규 - 트레이 상주 앱 (배포용 실제 산출물)
  Program.cs                  WinForms 진입점 (중복 실행 방지 뮤텍스 포함)
  TrayAppContext.cs           트레이 아이콘 + API 인프로세스 구동 + 자동 재연결 총괄
  DevicesForm.cs              트레이 클릭 시 뜨는 기기 목록 팝업 (동적으로 하나씩 채워짐)
  BridgeConnector.cs          벤더당 1대 제한을 지키며 연결하는 공통 헬퍼
  LastDeviceStore.cs          %AppData%\Migam\lastdevices.json 에 마지막 연결 기기 저장/로드
  IconFactory.cs              임시 로고(회색/파란색 원형 "M") 런타임 생성

scripts/
  install.ps1                 작업 스케줄러에 "로그온 시 자동 실행" 등록
  uninstall.ps1                등록 해제 + 실행 중인 프로세스 종료
```

## 요구사항별 구현 위치

| 요구사항 | 구현 |
|---|---|
| 1. 기기 동적 표시(찾는 대로 하나씩) | `NixDriver.DeviceDiscovered` 이벤트 → `DevicesForm`이 실시간으로 행 추가 |
| 2. nix/xrite 각 1대 제한 | `DeviceRouter.ConnectAsync`(API 경로), `BridgeConnector.ConnectAsync`(트레이 내부 경로) 양쪽에서 동일하게 강제. X-Rite는 애초에 후보가 1개뿐이라 자연히 성립 |
| 3. Connect 속도 개선 + 자동 재연결 | `NixDriver.QuickConnectAsync` - 전체 스캔(15초+)을 기다리지 않고 목표 기기가 잡히는 즉시 연결. `TrayAppContext.AutoReconnectAsync`가 시작 시 `LastDeviceStore`에 저장된 기기로 자동 실행 |
| 4. 트레이 아이콘 색상 변경 | `TrayAppContext.UpdateIconAndPersist` - 연결 수 0 → 회색, 1대 이상 → 파란색. `IconFactory`가 임시 아이콘을 그림 |

## 임시 로고 → 실제 로고로 교체하는 법

지금은 `IconFactory.cs`가 실행 중에 GDI+로 원형 "M" 아이콘(회색/파란색)을 그려서 쓰고 있다.
실제 Migam 로고 파일이 준비되면:

1. 로고를 회색 버전 / 파란색 버전 두 개의 `.ico`로 준비 (16~32px 권장, 트레이 아이콘 크기)
2. `MigamBridge.Tray/Assets/tray_gray.ico`, `tray_blue.ico`로 추가
3. `MigamBridge.Tray.csproj`에 추가:
   ```xml
   <ItemGroup>
     <None Include="Assets\*.ico">
       <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
     </None>
   </ItemGroup>
   ```
4. `TrayAppContext.cs`, `IconFactory.cs`에서 `IconFactory.CreateCircleIcon(...)` 호출을
   `new Icon("Assets/tray_gray.ico")` / `new Icon("Assets/tray_blue.ico")`로 교체

## 빌드 & 배포 (실제 Windows 개발 PC에서)

이 샌드박스는 nuget.org 접근이 막혀 있어 실제 컴파일 검증은 못 했음 — 아래 순서로 진행해줘.

```powershell
# 1. 복원 & 빌드 확인
dotnet restore
dotnet build MigamBridge.Tray -c Release

# 2. 단일 배포 폴더로 게시 (self-contained, 단일 exe)
dotnet publish MigamBridge.Tray -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -o .\publish

# 3. eXact.dll이 게시 폴더에 같이 있는지 확인 (native\eXact.dll → publish\eXact.dll)
#    csproj의 CopyToOutputDirectory 설정으로 자동 복사되어야 함. 없으면 수동으로 복사.

# 4. 로그온 시 자동 실행 등록 (관리자 PowerShell)
cd .\publish
..\scripts\install.ps1
```

## 확인해야 할 점 (실제 PC에서 검증 필요)

- `Nix:LicenseOptions`/`Nix:LicenseSignature`가 `appsettings.json`에 있는데, `dotnet publish`가
  이 파일을 게시 폴더로 복사하는지 확인(기본적으로 `Copy if newer` 설정이면 자동 복사됨).
- `QuickConnectAsync`의 5초 타임아웃은 임의로 잡은 값 — 실제 BLE 스캔 환경에서 너무 짧으면
  `TrayAppContext.AutoReconnectAsync`의 `TimeSpan.FromSeconds(5)`를 늘려서 조정.
- 트레이 앱이 관리자 권한 없이도 BLE/Bluetooth·USB 기기에 접근 가능한지 확인
  (`install.ps1`은 `-RunLevel Limited`로 등록 — 일반 사용자 권한으로 실행됨).
