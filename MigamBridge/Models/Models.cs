namespace MigamBridge.Models;

/// <summary>
/// 벤더(Nix/X-Rite)와 무관하게 통일된 측정 결과 스키마.
/// 파장 정규화(400-700nm×31 등)는 여기서 하지 않고, wavelength_start/step을 그대로
/// 실어 보내서 migam_svr(Python 백엔드)의 기존 반사율 처리 로직이 리샘플링을 전담한다.
/// </summary>
public record MeasurementResult(
    string DeviceId,
    string Vendor,           // "nix" | "xrite"
    DateTimeOffset MeasuredAt,
    int WavelengthStartNm,   // 예: 400 또는 380
    int WavelengthStepNm,    // 예: 10
    double[] Reflectance,    // 0.0 ~ 1.0 스케일 (원시값이 0~100이면 드라이버에서 /100 정규화)
    string? MeasurementMode = null  // 실제 반환된 모드 (예: "M0", "M1") — 지원 안 하면 null
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
