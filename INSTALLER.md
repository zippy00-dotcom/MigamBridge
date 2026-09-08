# 인스톨러 만들기 (Setup.exe)

## 방식 요약

```
소스코드 → dotnet publish(단일 exe) → Inno Setup(스크립트 1개) → MigamBridgeSetup.exe
```

Inno Setup을 쓰는 이유: 무료, 스크립트 하나로 끝, WiX/MSIX보다 훨씬 간단함.
"로그인 시 자동 실행"은 진짜 서비스 등록이 아니라 **시작프로그램 폴더에 바로가기를 넣는 방식**으로
처리했음(트레이 앱 특성상 이게 표준적인 방법 - `README_TRAY.md` 참고).

## 1단계 — 실제 Windows PC에서 빌드 확인

이 샌드박스는 nuget.org 접근이 막혀 있어서 컴파일 검증을 못 했음. 반드시 먼저:

```powershell
cd MigamBridge.Tray
dotnet restore
dotnet build -c Release
```

에러 없이 빌드되는지 확인한 뒤 다음 단계로.

## 2단계 — self-contained 단일 exe로 게시(publish)

```powershell
cd MigamBridge.Tray
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -o .\publish
```

- `--self-contained true` : 설치 대상 PC에 .NET 런타임이 없어도 바로 실행되도록 런타임까지 묶음
- `-p:PublishSingleFile=true` : 결과물을 exe 하나(+ 일부 네이티브 dll)로 뭉침
- 게시 후 `MigamBridge.Tray\publish` 폴더에 `MigamBridgeTray.exe`, `eXact.dll`, `appsettings.json`이
  같이 있는지 꼭 확인할 것 (특히 `eXact.dll`이 빠지면 X-Rite 연결이 전부 실패함)

```powershell
dir .\publish
```

## 3단계 — Inno Setup 설치

https://jrsoftware.org/isdl.php 에서 무료로 다운로드/설치 (Inno Setup 6 기준).

## 4단계 — 스크립트로 컴파일

이미 만들어둔 `installer\MigamBridge.iss`를 Inno Setup으로 열고 `Ctrl+F9`(Compile) 누르면 끝.

- 스크립트 안의 `#define PublishDir "..\MigamBridge.Tray\publish"` 경로가 실제 폴더 위치와
  맞는지만 확인(폴더 구조를 그대로 유지했다면 수정 불필요)
- 컴파일 완료되면 `installer\Output\MigamBridgeSetup.exe` 생성됨 — 이게 배포할 인스톨러 파일

## 5단계 — 설치 테스트

`MigamBridgeSetup.exe`를 다른 PC(또는 같은 PC)에서 실행:

1. 관리자 권한 요청 뜸 (Program Files에 설치하기 위함) → 허용
2. `C:\Program Files\Migam\MigamBridgeTray`에 설치됨
3. 시작프로그램 바로가기 생성됨 (`%AppData%\Microsoft\Windows\Start Menu\Programs\Startup`)
4. 설치 마지막에 "설치 후 바로 실행" 체크박스 → 체크하면 즉시 트레이 아이콘이 뜸
5. 로그아웃 후 다시 로그인해서 자동으로 트레이에 뜨는지 확인

## 제거(테스트용)

Windows 설정 > 앱 > "Migam 측색기 브리지" 제거. 실행 중이던 트레이 앱은
`[UninstallRun]`에 넣어둔 `taskkill`이 자동으로 먼저 종료시킴.

## 버전 올릴 때

새 버전 배포할 때는 `MigamBridge.Tray.csproj`의 `<Version>1.0.0</Version>`과
`MigamBridge.iss`의 `#define MyAppVersion "1.0.0"`을 **둘 다** 올리고(반드시 같은 값으로),
1~2단계(publish)부터 다시 반복하면 됨.

## 자동 업데이트 배포하기

트레이 앱은 시작 시 + 6시간마다 `appsettings.json`의 `Update:ManifestUrl`에 있는 JSON을 확인해서,
더 최신 버전이 있으면 조용히 받아서 설치하고 스스로 재시작함(사용자 확인 없이 완전 자동).

### 1. 새 버전 게시물 준비

위 "버전 올릴 때" 순서대로 새 `MigamBridgeSetup.exe`를 만들고 나면, 체크섬을 구해둘 것:

```powershell
Get-FileHash .\installer\Output\MigamBridgeSetup.exe -Algorithm SHA256
```

### 2. 버전 매니페스트 JSON 작성

아무 파일 이름이든 상관없음 (예: `version.json`). 형식:

```json
{
  "latest": "1.1.0",
  "url": "https://your-domain/migambridge/MigamBridgeSetup.exe",
  "sha256": "여기에_위에서_구한_해시값",
  "notes": "eXact 연결 안정성 개선"
}
```

- `latest`: 새 버전 번호 (csproj/iss의 버전과 동일하게)
- `url`: 새 `MigamBridgeSetup.exe`를 내려받을 수 있는 실제 URL (반드시 HTTPS 권장)
- `sha256`: 위에서 구한 해시값 — **없으면 검증을 건너뛰므로 꼭 넣을 것** (자동 설치라서 무결성 검증이 특히 중요함)

### 3. 호스팅 & 설정

`version.json`과 `MigamBridgeSetup.exe`를 같은 곳(mix 서버든 GitHub Releases든)에 올리고,
`appsettings.json`의 `Update:ManifestUrl`에 그 `version.json`의 URL을 넣으면 끝.

```json
"Update": {
  "ManifestUrl": "https://your-domain/migambridge/version.json"
}
```

이후 새 버전 낼 때마다: ①exe/iss 버전 올리기 → ②publish/컴파일 → ③해시 구하기 → ④version.json 갱신 → ⑤업로드.
이미 설치된 사용자들은 알아서 6시간 안에 자동으로 최신 버전으로 올라감(트레이 메뉴에서 수동 확인도 가능).

## 웹 콘솔 CORS 허용 도메인

`MigamBridge\appsettings.json`의 `Bridge:AllowedOrigins`에 실제 배포되는 웹 콘솔 도메인을 추가해야 함
(기본값은 개발용 `localhost:3000`만 들어있음):

```json
"Bridge": {
  "AllowedOrigins": [ "http://localhost:3000", "https://localhost:3000", "https://console.your-domain.com" ]
}
```

참고: Chrome/Edge 142부터는 HTTPS 웹페이지가 `127.0.0.1`(로컬 브리지)에 접속하려 하면
브라우저가 자체적으로 "로컬 네트워크 접근 허용" 팝업을 사용자에게 띄움(Local Network Access) —
이건 우리 코드가 아니라 브라우저가 강제하는 보안 계층이라 별도 대응 불필요. 단, Firefox/Safari는
아직 이 기능이 없어서 지원 대상에서 제외했음(현장은 Chrome/Edge만 쓰는 것으로 확인됨).

## 참고 — Task Scheduler 방식이 필요하다면

시작프로그램 바로가기 대신, 예전에 만들어둔 `scripts\install.ps1`(작업 스케줄러 "로그온 시 트리거"
등록) 방식을 쓰고 싶으면, `[Icons]`의 `{userstartup}` 줄을 지우고 `[Run]` 섹션에
`Filename: "powershell.exe"; Parameters: "-ExecutionPolicy Bypass -File ""{app}\install.ps1"""`
같은 줄을 추가하면 됨. 대부분의 경우엔 시작프로그램 바로가기 쪽이 더 간단하고 충분함.
