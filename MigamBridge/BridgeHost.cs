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
        var allowedOrigins = builder.Configuration.GetSection("Bridge:AllowedOrigins").Get<string[]>()
            ?? new[] { "http://localhost:3000", "https://localhost:3000" };
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

        app.UseCors();

        // ── Private Network Access(PNA) 프리플라이트 대응 ──
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.ContainsKey("Access-Control-Request-Private-Network"))
            {
                context.Response.Headers.Append("Access-Control-Allow-Private-Network", "true");
            }
            await next();
        });

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

        app.MapPost("/v1/devices/{id}/disconnect", async (string id, DeviceRouter router, CancellationToken ct) =>
        {
            await router.DisconnectAsync(id, ct);
            return Results.Ok();
        });
    }
}
