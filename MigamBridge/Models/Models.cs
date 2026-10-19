namespace MigamBridge.Models;

/// <summary>하나의 측정 조건(M0/M1/M2 등)에 대한 분광 반사율 데이터.</summary>
public record ModeMeasurement(
    string Mode,             // "M0", "M1" 등
    double[] Reflectance     // 0.0 ~ 1.0 스케일
);

/// <summary>
/// 벤더(Nix/X-Rite)와 무관하게 통일된 측정 결과 스키마.
/// 파장 정규화(400-700nm×31 등)는 여기서 하지 않고, wavelength_start/step을 그대로
/// 실어 보내서 migam_svr(Python 백엔드)의 기존 반사율 처리 로직이 리샘플링을 전담한다.
///
/// v1.0.4.0부터: 한 번의 물리 측정(버튼 누름)에 이미 M0/M1 등 여러 조건이 동시에 들어있다는
/// 점을 활용해, Modes에 여러 개를 한 번에 담아 반환한다(기본값: M0+M1 동시 측정).
/// mode 쿼리 파라미터로 특정 모드만(예: "M0" 또는 "M0,M2") 요청하면 그만큼만 채워진다.
/// </summary>
public record MeasurementResult(
    string DeviceId,
    string Vendor,           // "nix" | "xrite"
    DateTimeOffset MeasuredAt,
    int WavelengthStartNm,   // 예: 400 또는 380 (모든 모드가 같은 파장 범위를 공유한다고 가정)
    int WavelengthStepNm,    // 예: 10
    IReadOnlyList<ModeMeasurement> Modes  // 기본 [M0, M1]. 요청/지원 모드에 따라 1개~N개
);

public enum DeviceStatus
{
    Disconnected,
    Connecting,
    Connected,
    Error
}

public record DeviceInfo(
    string Id,
    string Vendor,
    string Model,
    DeviceStatus Status,
    string? ErrorMessage = null
);

/// <summary>
/// 두 SDK(Nix .NET / X-Rite 네이티브)를 동일한 형태로 다루기 위한 공통 인터페이스.
/// 실제 SDK 호출 부분(Connect/Measure 내부)만 벤더별로 다르고,
/// Program.cs의 API 엔드포인트는 이 인터페이스만 알면 됨.
/// </summary>
public interface IColorimeterDriver
{
    string Vendor { get; }

    Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken ct);

    Task<DeviceInfo> ConnectAsync(string deviceId, CancellationToken ct);

    // mode: "M0"/"M1"/"M2"/"M3" 등 원하는 측정 조건. null이면 드라이버가 기본값(첫 번째로 발견되는 것)을 사용.
    Task<MeasurementResult> MeasureAsync(string deviceId, string? mode, CancellationToken ct);

    Task DisconnectAsync(string deviceId, CancellationToken ct);
}

/// <summary>
/// 물리 버튼으로 트리거되는 기기(X-Rite eXact 등) 전용 선택적 인터페이스.
/// 기존 MeasureAsync는 "대기하다가 한 번 눌리면 그 값을 반환하고 끝"이지만,
/// 이건 "대기 상태를 유지한 채로 눌릴 때마다 계속 콜백으로 값을 전달"하는 연속 캡처 모드다.
/// 소프트웨어가 직접 트리거하는 기기(Nix 등)는 이 개념이 없어 구현하지 않아도 됨.
/// </summary>
public interface IContinuousCaptureDriver
{
    /// <summary>
    /// 호출자가 ct를 취소할 때까지(예: 스트리밍 연결 종료) 대기를 유지하며,
    /// 물리 버튼이 눌려 새 측정이 감지될 때마다 onMeasurement를 호출한다.
    /// 시간 제한(timeout)은 없음 - 계속 대기 상태로 남아있는 것이 정상 동작.
    /// </summary>
    Task ListenForMeasurementsAsync(string deviceId, string? mode, Func<MeasurementResult, Task> onMeasurement, CancellationToken ct);
}
