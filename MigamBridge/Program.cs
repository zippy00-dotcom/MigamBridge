using MigamBridge;

// 독립 실행형(콘솔/디버그) 진입점. 실제 배포는 MigamBridge.Tray가 이 API를
// 자기 프로세스 안에서 인프로세스로 띄우는 방식을 쓰지만, 개발 중 이 프로젝트만
// 단독으로 실행해서 API를 직접 테스트하고 싶을 때는 이 Program.cs로 그대로 실행 가능.
var app = BridgeHost.BuildApp(args);
app.Run("http://127.0.0.1:47990");
