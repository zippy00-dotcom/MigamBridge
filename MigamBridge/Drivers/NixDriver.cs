using MigamBridge.Models;
using NixUniversalSDK;
// NixUniversalSDK도 자체 DeviceStatus enum(Success/Failure 등 연결결과 코드)을 갖고 있어서
// 우리 Models.DeviceStatus(Connected/Disconnected 등 상태값)와 이름이 겹침 → 별칭으로 구분.
using NixDeviceStatus = NixUniversalSDK.DeviceStatus;

namespace MigamBridge.Drivers;

/// <summary>
/// Nix Universal SDK(.NET, NuGet: NixUniversalSDK 4.2.4) 래퍼.
/// NixExampleConsole 예제의 Connect/Measure 흐름을 그대로 따름.
///
/// [트레이 앱 지원을 위해 추가된 것]
/// - DeviceDiscovered: 스캔 중 기기를 하나 찾을 때마다(=BLE 어드버타이즈 수신 시마다) 즉시 발생.
///   Devices 팝업이 "찾는 대로 하나씩" 리스트에 추가하는 데 사용.
/// - ConnectionChanged: 연결/해제가 실제로 완료됐을 때 발생. 트레이 아이콘 색상 갱신에 사용.
/// - QuickConnectAsync: 프로그램 시작 시 "마지막에 쓰던 기기"로 빠르게 재연결하기 위한 경로.
///   전체 스캔(수십 초)을 다 기다리지 않고, 목표 기기가 발견되는 즉시 스캔을 멈추고 연결한다.
/// - IsConnected / ConnectedDeviceIds / DisconnectAllAsync: "벤더당 1대만 연결 가능" 규칙을
///   상위(DeviceRouter)에서 강제하기 위한 보조 API.
/// </summary>
public class NixDriver : IColorimeterDriver, IDisposable
{
    public string Vendor => "nix";

    private readonly IDeviceScanner _scanner;
    private readonly Dictionary<string, IDeviceCompat> _seenDevices = new();
    private readonly Dictionary<string, IDeviceCompat> _connectedDevices = new();

    /// <summary>스캔 중 기기가 하나 발견될 때마다 즉시 발생 (아직 미연결 상태로).</summary>
    public event Action<DeviceInfo>? DeviceDiscovered;

    /// <summary>연결 또는 해제가 실제로 완료된 시점에 발생.</summary>
    public event Action<DeviceInfo>? ConnectionChanged;

    /// <summary>
    /// 전원이 꺼지는 등 예기치 않게 연결이 끊긴 기기를 목록에서 완전히 제거하고 싶을 때 발생.
    /// (사용자가 직접 해제한 경우는 발생하지 않음 - 그 경우는 다시 연결할 수 있어야 하므로 목록에 남겨둠)
    /// UI는 이 이벤트를 받으면 해당 device_id의 행을 리스트에서 지우면 됨.
    /// </summary>
    public event Action<string>? DeviceRemoved;

    public bool IsConnected => _connectedDevices.Count > 0;
    public IReadOnlyCollection<string> ConnectedDeviceIds => _connectedDevices.Keys.ToList();

    /// <summary>지금 연결된 Nix 기기 목록을 스캔 없이 즉시 반환 (조회 API용).</summary>
    public IReadOnlyList<DeviceInfo> GetConnectedDeviceInfos() =>
        _connectedDevices.Select(kv => new DeviceInfo(kv.Key, "nix", kv.Value.Name, MigamBridge.Models.DeviceStatus.Connected)).ToList();

    public NixDriver(IConfiguration config)
    {
        // ⚠️ 라이선스 Options/Signature는 코드에 하드코딩하지 말고 appsettings.json이나
        // 환경변수(NIX_LICENSE_OPTIONS / NIX_LICENSE_SIGNATURE)로 주입할 것.
        var options = config["Nix:LicenseOptions"] ?? throw new InvalidOperationException("Nix:LicenseOptions 설정 필요");
        var signature = config["Nix:LicenseSignature"] ?? throw new InvalidOperationException("Nix:LicenseSignature 설정 필요");

        _ = LicenseManager.Activate(options: options, signature: signature);
        Console.WriteLine($"[Nix] LicenseManager.State = {LicenseManager.State}");

        _scanner = new DeviceScanner();
        _scanner.ScanResult += OnScanResult;
        _scanner.ScannerCreated += (s, e) => Console.WriteLine("[Nix] ScannerCreated");
        _scanner.ScannerStarted += (s, e) => Console.WriteLine("[Nix] ScannerStarted");
        _scanner.ScannerStopped += (s, e) => Console.WriteLine("[Nix] ScannerStopped");
    }

    private void OnScanResult(object? sender, ScanResultEventArgs args)
    {
        if (args.Device is IDeviceCompat device)
        {
            _seenDevices[device.Id] = device;
            DeviceDiscovered?.Invoke(new DeviceInfo($"nix-{device.Id}", "nix", device.Name, MigamBridge.Models.DeviceStatus.Disconnected));
        }
    }

    public async Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken ct)
    {
        await _scanner.InitializeAsync();

        _seenDevices.Clear();

        var completionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStopped(object? s, EventArgs e)
        {
            _scanner.ScannerStopped -= OnStopped;
            completionSource.TrySetResult(true);
        }
        _scanner.ScannerStopped += OnStopped;

        var scanPeriodMs = DeviceScanner.DefaultGeneralScanPeriodMs;
        _scanner.Start(scanPeriodMs);
        // SDK가 실제로 스캔에 쓰는 시간(scanPeriodMs)보다 짧게 기다리면 아직 끝나지도 않은 스캔을
        // 우리가 먼저 타임아웃으로 포기하게 됨 → 스캔 주기 + 여유(5초)만큼 기다리도록 수정.
        await completionSource.Task.WaitAsync(TimeSpan.FromMilliseconds(scanPeriodMs + 5000), ct);

        return _seenDevices.Values
            .Select(d => new DeviceInfo($"nix-{d.Id}", "nix", d.Name, MigamBridge.Models.DeviceStatus.Disconnected))
            .ToList();
    }

    public async Task<DeviceInfo> ConnectAsync(string deviceId, CancellationToken ct)
    {
        var rawId = StripPrefix(deviceId);
        if (!_seenDevices.TryGetValue(rawId, out var device))
        {
            // 스캔을 아직 안 했으면 한 번 실행
            await ListDevicesAsync(ct);
            if (!_seenDevices.TryGetValue(rawId, out device))
                throw new InvalidOperationException($"기기 {deviceId}를 스캔 목록에서 찾을 수 없음. 먼저 /v1/devices로 스캔했는지 확인.");
        }

        return await ConnectFoundDeviceAsync(rawId, device, ct);
    }

    /// <summary>
    /// 마지막으로 연결했던 기기로 빠르게 재연결하기 위한 경로.
    /// 이번 프로세스에서 이미 한 번이라도 스캔에 잡힌 적 있으면(=_seenDevices에 있으면) 스캔 없이 바로 연결.
    /// 없으면 스캔을 시작하되, 목표 rawId가 발견되는 즉시 스캔을 멈추고 연결한다(전체 스캔 주기를 기다리지 않음).
    /// </summary>
    /// <param name="rawId">"nix-" 접두사를 뗀 원본 기기 ID</param>
    /// <param name="timeout">목표 기기를 못 찾았을 때 포기할 시간</param>
    public async Task<DeviceInfo?> QuickConnectAsync(string rawId, TimeSpan timeout, CancellationToken ct)
    {
        if (_connectedDevices.TryGetValue($"nix-{rawId}", out var already))
            return new DeviceInfo($"nix-{rawId}", "nix", already.Name, MigamBridge.Models.DeviceStatus.Connected);

        if (_seenDevices.TryGetValue(rawId, out var cached))
            return await ConnectFoundDeviceAsync(rawId, cached, ct);

        await _scanner.InitializeAsync();

        var tcs = new TaskCompletionSource<IDeviceCompat>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnQuickMatch(object? s, ScanResultEventArgs e)
        {
            if (e.Device is IDeviceCompat d && string.Equals(d.Id, rawId, StringComparison.OrdinalIgnoreCase))
                tcs.TrySetResult(d);
        }

        _scanner.ScanResult += OnQuickMatch;
        try
        {
            _scanner.Start(DeviceScanner.DefaultGeneralScanPeriodMs);
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout, ct));
            try { _scanner.Stop(); } catch { /* 이미 멈춰있을 수 있음 - 무시 */ }

            if (completed != tcs.Task)
                return null; // 타임아웃 - 목표 기기를 못 찾음 (전원 꺼짐/범위 밖 등)

            return await ConnectFoundDeviceAsync(rawId, tcs.Task.Result, ct);
        }
        finally
        {
            _scanner.ScanResult -= OnQuickMatch;
        }
    }

    private async Task<DeviceInfo> ConnectFoundDeviceAsync(string rawId, IDeviceCompat device, CancellationToken ct)
    {
        var status = await device.ConnectAsync();
        if (status != NixDeviceStatus.Success)
            throw new InvalidOperationException($"Nix 연결 실패: {status}");

        var deviceId = $"nix-{rawId}";
        _connectedDevices[deviceId] = device;

        // SDK가 제공하는 Disconnected 이벤트를 구독 - 전원 꺼짐/신호 끊김 등 예기치 않은 연결 해제를
        // 우리가 직접 폴링하지 않고도 즉시 감지할 수 있음. (수동 DisconnectAsync 호출 시에는
        // 아래에서 미리 구독 해제하므로 이 핸들러가 중복 실행되지 않음)
        device.Disconnected += OnDeviceUnexpectedlyDisconnected;

        var info = new DeviceInfo(deviceId, "nix", device.Name, MigamBridge.Models.DeviceStatus.Connected);
        ConnectionChanged?.Invoke(info);
        return info;
    }

    private void OnDeviceUnexpectedlyDisconnected(object? sender, NixUniversalSDK.DeviceStatusArgs args)
    {
        if (sender is not IDeviceCompat device) return;

        var deviceId = $"nix-{device.Id}";
        device.Disconnected -= OnDeviceUnexpectedlyDisconnected;
        _connectedDevices.Remove(deviceId);

        // 전원이 꺼진 기기는 다시 스캔되기 전까지 목록에 나타나면 안 되므로 캐시에서도 제거.
        _seenDevices.Remove(device.Id);

        ConnectionChanged?.Invoke(new DeviceInfo(deviceId, "nix", device.Name, MigamBridge.Models.DeviceStatus.Disconnected));
        DeviceRemoved?.Invoke(deviceId);
    }

    public async Task<MeasurementResult> MeasureAsync(string deviceId, string? mode, CancellationToken ct)
    {
        if (!_connectedDevices.TryGetValue(deviceId, out var device))
            throw new InvalidOperationException($"기기 {deviceId}가 연결되어 있지 않음");

        var result = await device.MeasureAsync();
        if (result.Status != CommandStatus.Success || result.Measurements is null)
            throw new InvalidOperationException($"Nix 측정 실패: {result.Status}");

        // 여러 측정 모드(M0/M1/M2 등)가 올 수 있음. mode가 지정되면 그 이름과 일치하는 키를 찾고,
        // 없으면 분광(SpectralData)을 제공하는 첫 번째를 기본값으로 사용.
        IMeasurementData measurement;
        string? resolvedMode;
        if (!string.IsNullOrWhiteSpace(mode))
        {
            var match = result.Measurements.FirstOrDefault(kv =>
                string.Equals(kv.Key.ToString(), mode, StringComparison.OrdinalIgnoreCase));
            if (match.Value is null)
                throw new InvalidOperationException(
                    $"요청한 모드 '{mode}'를 찾을 수 없음. 사용 가능한 모드: {string.Join(", ", result.Measurements.Keys)}");
            measurement = match.Value;
            resolvedMode = match.Key.ToString();
        }
        else
        {
            var match = result.Measurements.FirstOrDefault(kv => kv.Value.ProvidesSpectral);
            measurement = match.Value ?? throw new InvalidOperationException("분광(spectral) 데이터를 제공하는 측정 결과가 없음");
            resolvedMode = match.Key.ToString();
        }

        var spectral = measurement.SpectralData;
        var wavelengths = spectral.Lambda; // 예: [400, 410, ..., 700]
        var values = spectral.Value;       // 반사율. 0~1 스케일인지 0~100인지는 SDK 문서/실측으로 확인 필요

        var step = wavelengths.Length > 1 ? wavelengths[1] - wavelengths[0] : 10;

        return new MeasurementResult(
            deviceId,
            "nix",
            DateTimeOffset.UtcNow,
            WavelengthStartNm: wavelengths[0],
            WavelengthStepNm: step,
            Reflectance: values.Select(v => (double)v).ToArray(),
            MeasurementMode: resolvedMode
        );
    }

    public Task DisconnectAsync(string deviceId, CancellationToken ct)
    {
        if (_connectedDevices.TryGetValue(deviceId, out var device))
        {
            // 수동 해제는 예기치 않은 연결 끊김이 아니므로 핸들러를 먼저 떼서 중복 처리를 막고,
            // 목록에서도 지우지 않는다(기기는 여전히 물리적으로 존재하므로 다시 연결할 수 있어야 함).
            device.Disconnected -= OnDeviceUnexpectedlyDisconnected;
            device.Disconnect();
            _connectedDevices.Remove(deviceId);
            ConnectionChanged?.Invoke(new DeviceInfo(deviceId, "nix", device.Name, MigamBridge.Models.DeviceStatus.Disconnected));
        }
        return Task.CompletedTask;
    }

    /// <summary>벤더당 1대 제한을 강제하기 위해, 새로 연결하기 전에 기존 연결을 모두 끊는다.</summary>
    public async Task DisconnectAllAsync(CancellationToken ct)
    {
        foreach (var id in _connectedDevices.Keys.ToList())
            await DisconnectAsync(id, ct);
    }

    private static string StripPrefix(string deviceId) =>
        deviceId.StartsWith("nix-") ? deviceId["nix-".Length..] : deviceId;

    public void Dispose()
    {
        foreach (var device in _connectedDevices.Values)
        {
            try { device.Disconnect(); } catch { /* 종료 중 오류 무시 */ }
        }
    }
}
