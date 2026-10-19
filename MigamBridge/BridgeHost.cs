using MigamBridge.Drivers;
using MigamBridge.Models;

namespace MigamBridge;

/// <summary>
/// 기존 Program.cs의 내용을 그대로 옮겨온 것. 이렇게 메서드로 분리해두면
/// - 독립 실행형 Program.cs(콘솔/디버그용)
/// - MigamBridge.Tray(트레이 상주 앱, 같은 프로세스 안에서 API를 호스팅)
/// 양쪽에서 동일한 API 서버 구성을 재사용할 수 있다.
/// </summary>
public static class BridgeHost
{
    public static WebApplication BuildApp(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // appsettings.json은 GitHub(public 레포)에 커밋되므로 실제 Nix 라이선스 키를 담지 않는다.
        // 실제 키는 이 파일(appsettings.Local.json, .gitignore에 등록됨)이나 환경변수
        // (Nix__LicenseOptions / Nix__LicenseSignature)로 주입한다. 파일이 없어도 오류 없이 스킵됨.
        builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

        // ── 드라이버 등록 (벤더별 실제 SDK 래퍼) ──
        builder.Services.AddSingleton<NixDriver>();
        builder.Services.AddSingleton<XriteDriver>();

        // device_id의 접두사(nix-xxx / xrite-xxx)로 어떤 드라이버를 쓸지 결정하는 라우터
        builder.Services.AddSingleton<DeviceRouter>();

        // ── CORS: 로컬 웹 콘솔에서만 호출 허용 ──
        // 허용 도메인은 appsettings.json의 Bridge:AllowedOrigins에서 읽는다.
        // (배포 도메인이 바뀌어도 재컴파일 없이 설정 파일만 고치면 됨)
        // 설정이 비어있을 때의 기본값. 사내망 시험 서버(192.168.0.11)의 웹 주소를 포함한다.
        // 웹 주소가 바뀌면 appsettings.json(또는 appsettings.Local.json)만 고치고 브리지를 재시작하면 됨.
        var allowedOrigins = builder.Configuration.GetSection("Bridge:AllowedOrigins").Get<string[]>();
        if (allowedOrigins is null || allowedOrigins.Length == 0)
        {
            allowedOrigins = new[]
            {
                "http://localhost:3000", "https://localhost:3000",
                "http://localhost:3001", "https://localhost:3001",
                "http://192.168.0.11:3000", "http://192.168.0.11:3001"
            };
        }
        // 설정 오타(끝의 '/' 등)로 Origin 비교가 어긋나는 것을 방지: 공백/끝 슬래시 제거
        allowedOrigins = allowedOrigins
            .Select(o => o.Trim().TrimEnd('/'))
            .Where(o => o.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Logger.Log($"[CORS] 허용 origin: {string.Join(", ", allowedOrigins)}");
        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                policy.WithOrigins(allowedOrigins)
                      .AllowAnyMethod()
                      .AllowAnyHeader();
            });
        });

        var app = builder.Build();

        // ── Private Network Access(PNA) 프리플라이트 대응 ──
        // ⚠️ 반드시 UseCors() "앞"에 둘 것: UseCors는 OPTIONS 사전 요청을 직접 응답하고 파이프라인을
        //    끝내버리기 때문에, 뒤에 두면 사전 요청에는 이 헤더가 절대 붙지 않는다.
        //    (192.168.x.x 같은 사설망 주소의 웹 → 127.0.0.1 브리지 호출 시 브라우저가 이 헤더를 요구함)
        //    허용 출처(Origin)일 때만 붙여서, 허용 목록 밖 사이트에는 PNA 허용을 주지 않는다.
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.ContainsKey("Access-Control-Request-Private-Network")
                && context.Request.Headers.TryGetValue("Origin", out var origin)
                && allowedOrigins.Contains(origin.ToString().TrimEnd('/'), StringComparer.OrdinalIgnoreCase))
            {
                context.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
            }
            await next();
        });

        app.UseCors();

        MapEndpoints(app);

        return app;
    }

    private static void MapEndpoints(WebApplication app)
    {
        app.MapGet("/v1/health", () => Results.Ok(new { status = "ok" }));

        app.MapGet("/v1/devices", async (DeviceRouter router, CancellationToken ct) =>
        {
            var all = new List<DeviceInfo>();
            foreach (var driver in router.AllDrivers)
            {
                try
                {
                    all.AddRange(await driver.ListDevicesAsync(ct));
                }
                catch (Exception ex)
                {
                    // 한쪽 벤더 SDK가 실패해도 다른 쪽 목록은 반환되도록 개별 처리
                    all.Add(new DeviceInfo("unknown", driver.Vendor, "?", DeviceStatus.Error, ex.Message));
                }
            }
            return Results.Ok(all);
        });

        // 지금 연결되어 있는 기기의 종류/상태만 빠르게 확인하는 API - 스캔을 하지 않아 가볍고,
        // 외부 웹 콘솔이나 다른 프로그램이 주기적으로 폴링해서 "지금 nix/eXact가 연결돼있나?"를
        // 확인하는 용도로 쓰기 좋음.
        app.MapGet("/v1/devices/connected", (DeviceRouter router) => Results.Ok(router.GetConnectedDevices()));

        // 연결은 DeviceRouter.ConnectAsync를 통해서만 수행 - 벤더당 1대 제한이 여기서 강제됨.
        app.MapPost("/v1/devices/{id}/connect", async (string id, DeviceRouter router, CancellationToken ct) =>
        {
            try
            {
                var result = await router.ConnectAsync(id, ct);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: 500);
            }
        });

        app.MapPost("/v1/devices/{id}/measure", async (string id, string? mode, DeviceRouter router, CancellationToken ct) =>
        {
            try
            {
                var driver = router.Resolve(id);
                var result = await driver.MeasureAsync(id, mode, ct);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: 500);
            }
        });

        // 연속 캡처(SSE 스트림): 대기 상태를 유지한 채 물리 버튼을 여러 번 눌러도
        // 누를 때마다 새 측정값을 실시간으로 하나씩 밀어준다. 이 요청/연결이 살아있는 동안
        // 서버는 계속 대기하고, 클라이언트가 연결을 닫으면(브라우저 fetch abort, EventSource.close() 등)
        // 그 시점에 대기를 멈춘다. 현재는 X-Rite eXact(IContinuousCaptureDriver 구현체)만 지원.
        app.MapGet("/v1/devices/{id}/measure/stream", async (HttpContext context, string id, string? mode, DeviceRouter router) =>
        {
            IColorimeterDriver driver;
            try
            {
                driver = router.Resolve(id);
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync(ex.Message);
                return;
            }

            if (driver is not IContinuousCaptureDriver continuous)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync($"기기 '{id}'({driver.Vendor})는 연속 캡처를 지원하지 않음 (X-Rite eXact 전용)");
                return;
            }

            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            // 리버스 프록시(nginx 등)가 응답을 버퍼링해서 실시간 전달을 막는 것을 방지.
            context.Response.Headers["X-Accel-Buffering"] = "no";

            var ct = context.RequestAborted; // 클라이언트가 연결을 끊으면 자동으로 취소됨

            try
            {
                await continuous.ListenForMeasurementsAsync(id, mode, async result =>
                {
                    var json = System.Text.Json.JsonSerializer.Serialize(result);
                    await context.Response.WriteAsync($"data: {json}\n\n", ct);
                    await context.Response.Body.FlushAsync(ct);
                }, ct);
            }
            catch (OperationCanceledException)
            {
                // 클라이언트가 스트림 연결을 정상적으로 끊음 - 별도 처리 불필요
            }
            catch (Exception ex)
            {
                try
                {
                    var errJson = System.Text.Json.JsonSerializer.Serialize(new { error = ex.Message });
                    await context.Response.WriteAsync($"event: error\ndata: {errJson}\n\n", ct);
                    await context.Response.Body.FlushAsync(ct);
                }
                catch
                {
                    // 이미 끊긴 연결에 쓰기 실패 - 무시
                }
            }
        });

        app.MapPost("/v1/devices/{id}/disconnect", async (string id, DeviceRouter router, CancellationToken ct) =>
        {
            await router.DisconnectAsync(id, ct);
            return Results.Ok();
        });
    }
}
