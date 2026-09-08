# GitHub에 올리기 (zippy00-dotcom/MigamBridge)

## ⚠️ 먼저 확인 — Public 레포라서 중요함

레포가 **Public**이라고 했으니, `appsettings.json`에 있던 실제 Nix 라이선스 키는 이번에 빈 문자열로
바꿔뒀어. 실제 키 값은 별도 파일 `MigamBridge\appsettings.Local.json`에 넣어뒀고, 이 파일은
`.gitignore`에 등록해서 **깃에는 절대 안 올라가게** 했어.

**주의할 점 한 가지**: 이전에 내가 전달했던 zip 파일들에는 `appsettings.json`에 실제 키가 그대로
박혀있었어. 혹시 그 예전 zip을 풀어놓은 폴더가 지금 작업 폴더(`C:\migam_dev\migambridge`)와 다른 곳에
남아있다면, **git add 하기 전에 반드시 지금 이 zip의 `appsettings.json`(빈 값)으로 교체됐는지 확인**해줘.
안 그러면 예전 키가 그대로 커밋될 수 있어.

## 동작 원리

- `MigamBridge\appsettings.json` → 깃에 커밋됨, Nix 키는 빈 값
- `MigamBridge\appsettings.Local.json` → 깃에 안 올라감(.gitignore), 실제 키가 여기 들어있음
- 앱 실행 시 `BridgeHost.cs`가 `appsettings.json`을 읽은 뒤 `appsettings.Local.json`이 있으면
  그 값으로 덮어씀 → 로컬/배포 PC에서는 지금처럼 그대로 잘 동작함
- CI 서버나 이 파일을 두기 싫은 환경이라면, 환경변수 `Nix__LicenseOptions` / `Nix__LicenseSignature`
  를 설정해도 동일하게 동작함 (ASP.NET Core 설정 시스템이 환경변수도 자동으로 읽음)

## 처음 올리기 (레포에 아직 아무 커밋도 없는 경우)

```powershell
cd C:\migam_dev\migambridge

git init
git branch -M main
git remote add origin https://github.com/zippy00-dotcom/MigamBridge.git

git add .
git commit -m "Initial commit: MigamBridge + Tray v1.0.1.0"

git push -u origin main
```

## 만약 GitHub에서 레포 만들 때 "Initialize with README" 체크했다면

그 경우 레포에 이미 커밋이 하나 있어서 위의 마지막 `push`가 거부될 수 있어(non-fast-forward).
이럴 땐:

```powershell
git pull origin main --allow-unrelated-histories
# 혹시 README.md 충돌나면 열어서 정리하고 저장 후:
git add .
git commit -m "Merge remote README"
git push -u origin main
```

## 이후 버전 올릴 때마다

```powershell
git add .
git commit -m "v1.0.2.0: ..."
git push
```

## (선택) 커밋 전 마지막 체크리스트

```powershell
# appsettings.json에 실제 키가 안 남아있는지 확인
type MigamBridge\appsettings.json

# .gitignore가 appsettings.Local.json을 실제로 무시하는지 확인 (아무 출력도 없어야 정상)
git status --ignored | findstr Local
```
