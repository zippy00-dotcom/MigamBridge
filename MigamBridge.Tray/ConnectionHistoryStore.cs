using System.Text.Json;

namespace MigamBridge.Tray;

/// <summary>
/// %AppData%\Migam\connectionhistory.json 에 "한 번이라도 연결에 성공했던 기기 ID" 전체 목록을 저장/로드.
/// LastDeviceStore(벤더별로 마지막 1개만 기억, 자동 재연결용)와는 목적이 다름 -
/// 여기는 지금까지 스쳐간 모든 기기(Nix는 MAC이 다른 여러 개일 수 있음)를 계속 누적 기록해서,
/// DevicesForm 목록에서 "예전에 연결해본 적 있는 기기"를 옅은 파란색으로 구분 표시하는 데 쓴다.
/// 연결 해제되어도 이 기록은 지우지 않는다(이력이므로).
/// </summary>
internal static class ConnectionHistoryStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Migam", "connectionhistory.json");

    public static HashSet<string> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var json = File.ReadAllText(FilePath);
            var ids = JsonSerializer.Deserialize<string[]>(json);
            return ids is null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // 파일이 깨져있거나 접근 불가 - 이력 표시만 안 될 뿐이므로 조용히 무시
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static void Save(HashSet<string> deviceIds)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(deviceIds));
        }
        catch
        {
            // 저장 실패는 무시 - 다음 실행 때 이력 표시만 유실될 뿐, 프로그램 동작에는 지장 없음
        }
    }
}
