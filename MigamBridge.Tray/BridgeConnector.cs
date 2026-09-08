using MigamBridge.Drivers;
using MigamBridge.Models;

namespace MigamBridge.Tray;

/// <summary>
/// UI(DevicesForm)와 자동 재연결(TrayAppContext) 양쪽에서 공통으로 쓰는 연결 진입점.
/// "벤더당 1대만 연결 가능"을 여기서 강제한다 - 새로 연결하기 전에 같은 벤더의 기존 연결을 끊음.
/// (같은 규칙이 MigamBridge/DeviceRouter.cs에도 있음 - API 호출 경로에서 강제되는 것과 동일한 정책을
/// 트레이 앱의 인프로세스 호출 경로에서도 그대로 적용하기 위함)
/// </summary>
internal static class BridgeConnector
{
    public static async Task<DeviceInfo> ConnectAsync(NixDriver nix, XriteDriver xrite, string deviceId, CancellationToken ct)
    {
        if (deviceId.StartsWith("nix-", StringComparison.OrdinalIgnoreCase))
        {
            await nix.DisconnectAllAsync(ct);
            return await nix.ConnectAsync(deviceId, ct);
        }
        if (deviceId.StartsWith("xrite-", StringComparison.OrdinalIgnoreCase))
        {
            return await xrite.ConnectAsync(deviceId, ct);
        }
        throw new ArgumentException($"알 수 없는 device_id: {deviceId}");
    }
}
