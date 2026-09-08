using System.Runtime.InteropServices;
using MigamBridge.Models;
// Logger는 MigamBridge(루트) 네임스페이스에 있음 - 이 파일은 MigamBridge.Drivers 하위라 명시적 using 필요
using MigamBridge;

namespace MigamBridge.Drivers;

/// <summary>
/// X-Rite eXact SDK(eXact.dll, C/C++ export) P/Invoke 래퍼.
/// Examples/SDKTest의 Spectro.h/Spectro.cpp에서 export 함수 이름·시그니처를 확인:
///   - eXact.dll을 LoadLibrary+GetProcAddress로 로드하지만, 이름 그대로 __stdcall export이므로
///     C#에서는 [DllImport]로 직접 P/Invoke 가능 (별도 LoadLibrary 불필요).
///   - Connect()는 인자 없이 호출 — DLL 내부적으로 페어링된 인터페이스(BT/USB)를 자동 탐색/연결함.
///   - Measure()는 동기 호출로 보임(SDKTest 예제가 Measure() 직후 바로 GetReflData() 호출).
///     단, 안전하게 IsDataReady()로 짧게 폴링하는 루틴을 넣어둠.
///   - 배포 시 eXact.dll을 빌드 출력 폴더에 반드시 함께 둬야 함 (csproj에서 CopyToOutputDirectory 처리).
///   - 32비트/64비트 DLL이 예제에 각각 있었음 (Win32/ 와 x64/ 폴더) — 프로젝트 PlatformTarget과
///     반드시 동일한 비트수의 eXact.dll을 사용해야 함 (현재 csproj는 x64로 설정됨 → x64용 eXact.dll 사용).
/// </summary>
public class XriteDriver : IColorimeterDriver
{
    public string Vendor => "xrite";

    private const string DllName = "eXact.dll";

    // ── P/Invoke 선언 (Spectro.h 기준 실제 export 이름) ──
    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern bool Connect();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern bool Disconnect();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "IsConnected")]
    private static extern bool IsConnectedNative();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern bool Measure();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern bool IsDataReady();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int GetWavelengthCount();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int GetWavelengthValue(int index);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int GetSpectralSetCount();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr GetSpectralSetName(int dataSet); // const char* → IntPtr, Marshal.PtrToStringAnsi로 변환

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern float GetSpectralData(int dataSet, int wavelengthIndex);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr GetSerialNum();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr GetLastErrorString();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr GetAvailableSettings();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr GetSettingOptions(string setting);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr GetOption(string setting);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern bool SetOption(string setting, string option);

    // ── 인스턴스 상태 ──
    // eXact.dll은 프로세스 전역 상태(전역 함수, 인스턴스 개념 없음)라 동시에 한 대만 다룰 수 있음.
    // 여러 대를 동시에 쓰려면 X-Rite에 "여러 인스턴스 지원 여부"를 확인해야 함.
    private bool _connected;
    private const string SingleDeviceId = "xrite-exact1";

    // 연결에 성공했을 때의 방식(USB 또는 블루투스 페어링 이름)을 기억해뒀다가 화면에 표시하는 데 씀.
    private string _displayModel = "eXact";

    /// <summary>eXact는 스캔 개념이 없어 후보가 항상 1개뿐이라, 조회 시마다 즉시 한 번 발생.</summary>
    public event Action<DeviceInfo>? DeviceDiscovered;

    /// <summary>연결 또는 해제가 실제로 완료된 시점에 발생. 트레이 아이콘 색상 갱신에 사용.</summary>
    public event Action<DeviceInfo>? ConnectionChanged;

    /// <summary>
    /// 전원이 꺼지는 등 예기치 않게 연결이 끊긴 경우 발생 (백그라운드 폴링으로 감지).
    /// 사용자가 직접 해제한 경우는 발생하지 않음.
    /// </summary>
    public event Action<string>? DeviceRemoved;

    private CancellationTokenSource? _monitorCts;

    public bool IsConnected => _connected;

    /// <summary>지금 연결된 X-Rite 기기 목록을 즉시 반환 (조회 API용, 부수효과 없음).</summary>
    public IReadOnlyList<DeviceInfo> GetConnectedDeviceInfos() =>
        _connected
            ? new List<DeviceInfo> { new(SingleDeviceId, "xrite", _displayModel, DeviceStatus.Connected) }
            : new List<DeviceInfo>();

    public Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken ct)
    {
        // eXact.dll은 별도 "스캔" API가 없고 Connect()가 알아서 페어링된 기기를 찾음.
        // 그래서 여기서는 실제 스캔 없이 고정 ID 하나만 후보로 보여줌.
        var status = _connected ? DeviceStatus.Connected : DeviceStatus.Disconnected;
        var info = new DeviceInfo(SingleDeviceId, "xrite", _displayModel, status);
        DeviceDiscovered?.Invoke(info);
        IReadOnlyList<DeviceInfo> list = new List<DeviceInfo> { info };
        return Task.FromResult(list);
    }

    public Task<DeviceInfo> ConnectAsync(string deviceId, CancellationToken ct)
    {
        // ── 진단용: 연결 전에 사용 가능한 설정과 현재 값을 콘솔에 출력 ──
        try
        {
            var settingsRaw = SafePtrToString(GetAvailableSettings());
            Logger.Log($"[XRite] GetAvailableSettings() = \"{settingsRaw}\"");

            if (!string.IsNullOrWhiteSpace(settingsRaw))
            {
                var settingNames = settingsRaw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var name in settingNames)
                {
                    var current = SafePtrToString(GetOption(name));
                    Logger.Log($"[XRite] Setting '{name}' current value = \"{current}\"");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"[XRite] 설정 조회 중 예외 (무시하고 계속 진행): {ex.Message}");
        }

        // ⚠️ 이전 코드는 "USB가 아닌 옵션(블루투스 페어링 이름)"으로 매번 강제 전환했었는데,
        // 그러면 USB 케이블이 꽂혀있어도 무시하고 블루투스로만 연결을 시도하게 되어
        // (그 블루투스 기기가 꺼져있거나 범위 밖이면) USB 연결 자체가 안 되는 문제가 있었음.
        //
        // 지금은 특정 방식을 강제하지 않고: 1) 지금 설정된 Connection_Method 그대로 먼저 시도 →
        // 2) 실패하면 목록에 있는 나머지 옵션(USB 포함)을 하나씩 순서대로 시도해서, 실제로
        // 연결되는 방식(USB든 블루투스든)을 그때그때 자동으로 찾는다.
        string[] options = Array.Empty<string>();
        string? currentMethod = null;
        try
        {
            var methodOptionsRaw = SafePtrToString(GetSettingOptions("Connection_Method"));
            options = methodOptionsRaw.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            currentMethod = SafePtrToString(GetOption("Connection_Method"));
            Logger.Log($"[XRite] Connection_Method 가능한 옵션 = \"{methodOptionsRaw}\", 현재 값 = \"{currentMethod}\"");
        }
        catch (Exception ex)
        {
            Logger.Log($"[XRite] Connection_Method 조회 중 예외 (무시하고 기본 Connect()만 시도): {ex.Message}");
        }

        string? activeMethod;
        if (!TryConnectWithFallback(options, currentMethod, out activeMethod))
        {
            var err = SafePtrToString(GetLastErrorString());
            Logger.Log($"[XRite] 연결 실패 - 시도한 옵션: [{string.Join(", ", options)}], 마지막 오류: \"{err}\"");
            throw new InvalidOperationException($"X-Rite 연결 실패 (USB/블루투스 모두 시도함): {err}");
        }

        _connected = true;
        var serial = SafePtrToString(GetSerialNum());
        _displayModel = $"eXact (S/N {serial}, {DescribeConnectionMethod(activeMethod)})";
        var info = new DeviceInfo(SingleDeviceId, "xrite", _displayModel, DeviceStatus.Connected);
        ConnectionChanged?.Invoke(info);
        StartConnectionMonitor();
        return Task.FromResult(info);
    }

    /// <summary>
    /// Connection_Method 값을 사람이 읽기 좋은 라벨로 바꿈. eXact SDK는 "USB"라는 이름 그대로
    /// 나오지만, 블루투스는 별도 이름 없이 페어링된 기기 이름 자체가 옵션 값으로 나옴
    /// (예: "eXact_001695") - 그래서 "USB"가 아니면 전부 블루투스로 간주해서 표시한다.
    /// </summary>
    private static string DescribeConnectionMethod(string? method)
    {
        if (string.IsNullOrWhiteSpace(method)) return "연결방식 불명";
        return string.Equals(method, "USB", StringComparison.OrdinalIgnoreCase)
            ? "USB"
            : $"BT: {method}";
    }

    /// <summary>
    /// 현재 Connection_Method로 먼저 Connect()를 시도하고, 실패하면 나머지 옵션들을
    /// (USB 포함, 순서 그대로) 하나씩 SetOption + Connect()로 시도한다. 하나라도 성공하면 즉시 true이고,
    /// activeMethod에 실제로 성공한 방식의 값(예: "USB" 또는 블루투스 페어링 이름)이 담긴다.
    /// </summary>
    private bool TryConnectWithFallback(string[] options, string? currentMethod, out string? activeMethod)
    {
        if (Connect())
        {
            activeMethod = currentMethod;
            return true;
        }

        Logger.Log($"[XRite] 현재 설정({currentMethod})으로 연결 실패 - 다른 옵션으로 순서대로 재시도");

        foreach (var candidate in options)
        {
            if (string.Equals(candidate, currentMethod, StringComparison.OrdinalIgnoreCase))
                continue; // 이미 위에서 시도해서 실패한 값이므로 건너뜀

            try
            {
                var changed = SetOption("Connection_Method", candidate);
                Logger.Log($"[XRite] SetOption(Connection_Method, {candidate}) = {changed}, Connect() 재시도");
                if (Connect())
                {
                    activeMethod = candidate;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[XRite] '{candidate}' 시도 중 예외 (다음 옵션으로 계속): {ex.Message}");
            }
        }

        activeMethod = null;
        return false;
    }

    /// <summary>
    /// eXact.dll은 Nix SDK와 달리 "연결 끊김" 이벤트를 제공하지 않아서, 전원이 꺼지는 등
    /// 예기치 않은 연결 해제를 감지하려면 직접 주기적으로 확인하는 수밖에 없다.
    /// 연결된 동안 2초 간격으로 IsConnectedNative()를 확인하고, false로 바뀌면
    /// 연결이 끊긴 것으로 간주해 상태를 정리하고 UI에 알린다(목록에서 제거).
    /// </summary>
    private void StartConnectionMonitor()
    {
        StopConnectionMonitor();
        _monitorCts = new CancellationTokenSource();
        var token = _monitorCts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(2000, token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }

                if (token.IsCancellationRequested) break;

                bool stillConnected;
                try
                {
                    stillConnected = _connected && IsConnectedNative();
                }
                catch
                {
                    // DLL 호출 자체가 실패하면 연결이 끊긴 것으로 간주 (안전한 쪽으로 처리)
                    stillConnected = false;
                }

                if (!stillConnected && _connected)
                {
                    _connected = false;
                    _displayModel = "eXact";
                    ConnectionChanged?.Invoke(new DeviceInfo(SingleDeviceId, "xrite", _displayModel, DeviceStatus.Disconnected));
                    DeviceRemoved?.Invoke(SingleDeviceId);
                    break;
                }
            }
        }, token);
    }

    private void StopConnectionMonitor()
    {
        _monitorCts?.Cancel();
        _monitorCts?.Dispose();
        _monitorCts = null;
    }

    public async Task<MeasurementResult> MeasureAsync(string deviceId, string? mode, CancellationToken ct)
    {
        if (!_connected || !IsConnectedNative())
            throw new InvalidOperationException("X-Rite 기기가 연결되어 있지 않음");

        // ⚠️ 설계 변경: 소프트웨어에서 Measure()를 강제로 호출하지 않음.
        // eXact는 사용자가 대상물에 조리개를 대고 물리 버튼을 눌러야 정확한 위치에서 측정됨.
        // 원본 데모 앱(MainDlg::OnTimer)도 Measure()를 부르지 않고 IsDataReady()만
        // 백그라운드에서 계속 폴링하다가, 물리 버튼으로 측정이 완료되면 그 시점에 데이터를 읽음.
        // 그래서 여기서도 동일하게: 사용자가 버튼을 누를 때까지 기다렸다가 데이터를 읽는다.
        const int timeoutSeconds = 60; // 사용자가 위치 잡고 버튼 누를 시간 여유 (필요시 조정)
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        while (!IsDataReady())
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new InvalidOperationException(
                    $"{timeoutSeconds}초 동안 물리 버튼으로 측정이 감지되지 않음 (기기를 대상물에 대고 버튼을 눌러주세요)");

            ct.ThrowIfCancellationRequested();
            await Task.Delay(100, ct); // 데모 앱과 동일한 100ms 폴링 주기
        }

        int wlCount = GetWavelengthCount();
        if (wlCount <= 0)
            throw new InvalidOperationException("X-Rite: 유효하지 않은 파장 포인트 수");

        var wavelengths = new int[wlCount];
        for (int i = 0; i < wlCount; i++)
            wavelengths[i] = GetWavelengthValue(i);

        // eXact는 한 번 측정하면 M0/M1/M2/M3 같은 여러 조건의 데이터를 동시에 갖고 있고,
        // GetSpectralSetName(i)로 그 중 하나를 이름으로 골라서 읽는 구조.
        var setCount = GetSpectralSetCount();
        if (setCount <= 0)
            throw new InvalidOperationException("X-Rite: 유효하지 않은 데이터 세트 개수");

        var setNames = new string[setCount];
        for (int i = 0; i < setCount; i++)
            setNames[i] = SafePtrToString(GetSpectralSetName(i));

        int dataSet = 0;
        string? resolvedMode = setNames.Length > 0 ? setNames[0] : null;
        if (!string.IsNullOrWhiteSpace(mode))
        {
            var idx = Array.FindIndex(setNames, n => string.Equals(n, mode, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
                throw new InvalidOperationException(
                    $"요청한 모드 '{mode}'를 찾을 수 없음. 사용 가능한 모드: {string.Join(", ", setNames)}");
            dataSet = idx;
            resolvedMode = setNames[idx];
        }

        var reflectance = new double[wlCount];
        for (int i = 0; i < wlCount; i++)
        {
            // GetSpectralData가 0~100(%) 스케일인지 0~1인지는 실측으로 확인 필요.
            // 일단 0~100으로 가정하고 /100 정규화. 실측 후 다르면 이 줄만 수정.
            reflectance[i] = GetSpectralData(dataSet, i) / 100.0;
        }

        int step = wlCount > 1 ? wavelengths[1] - wavelengths[0] : 10;

        return new MeasurementResult(
            deviceId,
            "xrite",
            DateTimeOffset.UtcNow,
            WavelengthStartNm: wavelengths[0],
            WavelengthStepNm: step,
            Reflectance: reflectance,
            MeasurementMode: resolvedMode
        );
    }

    public Task DisconnectAsync(string deviceId, CancellationToken ct)
    {
        if (_connected)
        {
            StopConnectionMonitor();
            Disconnect();
            _connected = false;
            _displayModel = "eXact";
            ConnectionChanged?.Invoke(new DeviceInfo(SingleDeviceId, "xrite", _displayModel, DeviceStatus.Disconnected));
        }
        return Task.CompletedTask;
    }

    private static string SafePtrToString(IntPtr ptr) =>
        ptr == IntPtr.Zero ? string.Empty : (Marshal.PtrToStringAnsi(ptr) ?? string.Empty);
}
