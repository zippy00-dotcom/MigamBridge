using MigamBridge.Drivers;
using MigamBridge.Models;

namespace MigamBridge;

/// <summary>
/// device_id 접두사(nix- / xrite-)로 드라이버를 고르는 라우터.
///
/// [벤더당 1대 연결 제한]
/// ConnectAsync를 통해서만 연결하면, 같은 벤더의 새 기기를 연결하기 전에 기존 연결을
/// 자동으로 끊어서 "nix 1대 + xrite 1대"만 유지되도록 강제한다.
/// (X-Rite는 애초에 후보가 1개뿐이라 이 제약이 자연히 성립함)
/// </summary>
public class DeviceRouter
{
    private readonly NixDriver _nix;
    private readonly XriteDriver _xrite;

    /// <summary>어느 한쪽이든 연결/해제가 완료되면 발생 - 트레이 아이콘 갱신용.</summary>
    public event Action<DeviceInfo>? ConnectionChanged;

    public DeviceRouter(NixDriver nix, XriteDriver xrite)
    {
        _nix = nix;
        _xrite = xrite;
        _nix.ConnectionChanged += info => ConnectionChanged?.Invoke(info);
        _xrite.ConnectionChanged += info => ConnectionChanged?.Invoke(info);
    }

    public IEnumerable<IColorimeterDriver> AllDrivers => new IColorimeterDriver[] { _nix, _xrite };

    public int ConnectedCount => (_nix.IsConnected ? 1 : 0) + (_xrite.IsConnected ? 1 : 0);

    /// <summary>지금 연결된 기기 목록(벤더/모델/상태)을 스캔 없이 즉시 반환. 외부 웹/프로그램이 폴링하기 좋음.</summary>
    public IReadOnlyList<DeviceInfo> GetConnectedDevices()
    {
        var list = new List<DeviceInfo>();
        list.AddRange(_nix.GetConnectedDeviceInfos());
        list.AddRange(_xrite.GetConnectedDeviceInfos());
        return list;
    }

    public IColorimeterDriver Resolve(string deviceId)
    {
        if (deviceId.StartsWith("nix-", StringComparison.OrdinalIgnoreCase)) return _nix;
        if (deviceId.StartsWith("xrite-", StringComparison.OrdinalIgnoreCase)) return _xrite;
        throw new ArgumentException($"device_id '{deviceId}'의 벤더를 알 수 없음 (nix-/xrite- 접두사 필요)");
    }

    public async Task<DeviceInfo> ConnectAsync(string deviceId, CancellationToken ct)
    {
        if (deviceId.StartsWith("nix-", StringComparison.OrdinalIgnoreCase))
        {
            await _nix.DisconnectAllAsync(ct); // 벤더당 1대 제한 강제
            return await _nix.ConnectAsync(deviceId, ct);
        }
        if (deviceId.StartsWith("xrite-", StringComparison.OrdinalIgnoreCase))
        {
            return await _xrite.ConnectAsync(deviceId, ct);
        }
        throw new ArgumentException($"device_id '{deviceId}'의 벤더를 알 수 없음 (nix-/xrite- 접두사 필요)");
    }

    public Task DisconnectAsync(string deviceId, CancellationToken ct) => Resolve(deviceId).DisconnectAsync(deviceId, ct);
}
