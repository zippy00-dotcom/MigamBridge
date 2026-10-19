# MigamBridge

Nix Spectro 2(.NET SDK)와 X-Rite eXact 1(네이티브 eXact.dll)을 하나의 로컬 HTTP API로 통합하는
브리지 서버. `http://127.0.0.1:47990`에서 서비스하며, 웹 콘솔(Next.js)이 여기로 fetch해서
측정값을 가져간다.

## 실행 전 필수 설정

1. **Nix 라이선스**: `appsettings.json`의 `Nix:LicenseOptions` / `Nix:LicenseSignature`를
   실제 발급받은 값으로 교체. (`NixExampleConsole/LicenseKey.cs`에 있던 것과 동일한 값)
2. **PlatformTarget 확인**: `MigamBridge.csproj`의 `<PlatformTarget>x64</PlatformTarget>`가
   `native/eXact.dll`(x64용으로 이미 포함해둠)과 일치해야 함. 32비트로 빌드해야 하는 환경이면
   `Examples/SDKTest/Win32/Release/eXact.dll`(32비트용)로 교체하고 PlatformTarget도 `x86`으로 변경.
3. **빌드**: 이 샌드박스는 nuget.org 접근이 막혀 있어 `NixUniversalSDK` 패키지를 실제로
   복원(restore)/빌드 검증하지 못했음. **반드시 실제 Windows 개발 PC에서**
   `dotnet restore && dotnet build`로 먼저 컴파일 에러 여부를 확인할 것.

## 실측으로 검증해야 하는 부분 (코드에 TODO로 표시됨)

| 항목 | 파일 | 확인 방법 |
|---|---|---|
| `LicenseManager.State`의 실제 열거형 값 | `NixDriver.cs` | 연결 성공 후 `LicenseManager.State` 값을 콘솔에 찍어서 실제 이름 확인 |
| Nix `SpectralData.Value`가 0~1 스케일인지 0~100인지 | `NixDriver.cs` | 첫 측정 후 raw 값 로그로 확인, 필요시 정규화 추가 |
| X-Rite `GetSpectralData`가 0~1 스케일인지 0~100(%)인지 | `XriteDriver.cs` | 현재 `/100.0`으로 가정해둠 — 실측해서 틀리면 이 줄만 수정 |
| X-Rite의 `GetSpectralSetCount`가 1보다 클 때 어떤 dataSet(M0/M1 등)을 쓸지 | `XriteDriver.cs` | `GetSpectralSetName(i)`로 이름 찍어보고 원하는 모드로 `dataSet` 고정 |
| eXact.dll이 한 번에 기기 1대만 지원하는지(전역 상태) | `XriteDriver.cs` | 여러 대 동시 연결이 필요하면 X-Rite에 문의 필요 |

## 엔드포인트

```
GET  /v1/health
GET  /v1/devices                     → Nix는 실제 BLE 스캔, X-Rite는 고정 후보 1개(xrite-exact1) 반환
GET  /v1/devices/connected           → 지금 연결된 기기만 스캔 없이 즉시 반환 (외부 웹/프로그램 폴링용)
POST /v1/devices/{id}/connect
POST /v1/devices/{id}/measure        → 단발성: 버튼 1번 눌림을 기다렸다가 그 값 하나만 반환하고 끝
                                         mode 쿼리 파라미터 생략 시 기본으로 M0+M1을 한 번에 같이 반환.
                                         (한 번의 물리 측정에 이미 M0/M1이 동시에 들어있어서 기기를 두 번
                                          찍을 필요 없음) 특정 모드만 원하면 ?mode=M0 또는 ?mode=M0,M2 형태로 지정.
                                         { wavelength_start_nm, wavelength_step_nm,
                                           modes: [ { mode: "M0", reflectance: [...] },
                                                    { mode: "M1", reflectance: [...] } ] }
GET  /v1/devices/{id}/measure/stream → 연속 캡처(SSE): 대기 상태를 유지한 채 버튼을 여러 번 눌러도
                                         누를 때마다 매번 새 측정값을 하나씩 실시간으로 흘려보냄.
                                         mode 파라미터 규칙은 위 /measure와 동일(기본 M0+M1 동시 반환).
                                         현재 X-Rite eXact 전용(IContinuousCaptureDriver 구현체만 지원,
                                         Nix처럼 소프트웨어가 직접 트리거하는 기기는 해당 없음).
                                         클라이언트가 연결을 닫을 때까지(EventSource.close() 등) 계속 대기.
POST /v1/devices/{id}/disconnect
```

device_id는 `nix-<스캔된ID>` 또는 `xrite-exact1` 형태.

## 배포용 트레이 상주 앱

이 API를 우측 하단 트레이 아이콘으로 상주시키는 배포 버전은 `../MigamBridge.Tray` 프로젝트 참고.
빌드/게시/자동시작 등록 방법은 `../README_TRAY.md` 참고.

## 웹 콘솔 쪽 변경

기존 Web Bluetooth GATT 코드(`BluetoothPanel.tsx`)는 제거하고, 아래처럼 단순 fetch로 대체:

```ts
const res = await fetch("http://localhost:47990/v1/devices/xrite-exact1/measure", { method: "POST" });
const data = await res.json();
// data.modes 는 기본적으로 [{ mode: "M0", reflectance: [...] }, { mode: "M1", reflectance: [...] }]
const m0 = data.modes.find(m => m.mode === "M0")?.reflectance;
const m1 = data.modes.find(m => m.mode === "M1")?.reflectance;
setLastMeasurement({ m0, m1 });
```

### 대기 중 여러 번 측정 (연속 캡처)

버튼을 누를 때마다 매번 새 값을 받고 싶으면(예: 여러 샘플을 연속으로 찍는 화면) `POST .../measure`
대신 `EventSource`로 스트림을 열어두면 됨. 화면을 벗어나거나 그만 찍을 때 `es.close()`로 닫으면
서버 쪽 대기도 그 즉시 멈춤:

```ts
const es = new EventSource("http://localhost:47990/v1/devices/xrite-exact1/measure/stream");
es.onmessage = (e) => {
  const data = JSON.parse(e.data); // 버튼 누를 때마다 이 콜백이 매번 실행됨
  // data.modes 는 기본적으로 M0/M1이 같이 옴 - 필요한 모드만 골라 쓰면 됨
  addMeasurement(data.modes);
};
es.onerror = () => es.close();

// 그만 찍을 때
es.close();
```

## CORS 허용 웹 주소 설정

웹 콘솔의 접속 주소(Origin)가 `appsettings.json`의 `Bridge:AllowedOrigins`에 있어야 브라우저가 브리지 응답을 받아준다.
주소가 바뀌면(IP/포트/HTTPS) 이 목록에 정확히 추가하고 브리지를 재시작한다(재빌드 불필요).
기본값: `localhost:3000/3001`, `192.168.0.11:3000/3001`. `*`는 쓰지 말 것(인증 없는 로컬 프로그램이라 아무 사이트나 측색기를 호출할 수 있게 됨).
시작 로그에 `[CORS] 허용 origin: ...` 줄로 실제 적용된 목록이 남는다.
사설망(192.168.x.x) 웹 → 127.0.0.1 브리지 호출에 필요한 `Access-Control-Allow-Private-Network` 응답도 허용 출처에 한해 자동으로 붙는다.
