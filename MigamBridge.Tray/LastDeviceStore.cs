using System.Text.Json;

namespace MigamBridge.Tray;

/// <summary>마지막으로 연결에 성공했던 기기 ID들 (다음 실행 시 자동 재연결에 사용).</summary>
public record LastDevices(string? NixDeviceId, string? XriteDeviceId);

/// <summary>
/// %AppData%\Migam\lastdevices.json 에 마지막 연결 기기 정보를 저장/로드.
/// 연결 해제 시에도 이 정보는 지우지 않는다 - "마지막으로 쓰던 기기"로 남겨둬야
/// 다음 프로그램 시작 시 같은 기기로 자동 재연결을 시도할 수 있기 때문.
/// </summary>
internal static class LastDeviceStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Migam", "lastdevices.json");

    public static LastDevices Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new LastDevices(null, null);

            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<LastDevices>(json) ?? new LastDevices(null, null);
        }
        catch
        {
            // 파일이 깨져있거나 접근 불가 - 자동 재연결만 안 될 뿐이므로 조용히 무시
            return new LastDevices(null, null);
        }
    }

    public static void Save(LastDevices devices)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(devices));
        }
        catch
        {
            // 저장 실패는 무시 - 다음 실행 때 자동연결만 안 될 뿐, 프로그램 동작에는 지장 없음
        }
    }
}
