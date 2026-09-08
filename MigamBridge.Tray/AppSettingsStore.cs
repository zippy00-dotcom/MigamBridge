using System.Text.Json;

namespace MigamBridge.Tray;

internal record AppSettings(string Language);

/// <summary>%AppData%\Migam\settings.json 에 사용자가 선택한 언어를 저장/로드.</summary>
internal static class AppSettingsStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Migam", "settings.json");

    public static AppLanguage LoadLanguage()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings is not null && Enum.TryParse<AppLanguage>(settings.Language, out var lang))
                    return lang;
            }
        }
        catch
        {
            // 설정 파일이 깨져있거나 접근 불가 - 시스템 언어로 대체
        }

        return Strings.DetectSystemDefault();
    }

    public static void SaveLanguage(AppLanguage language)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new AppSettings(language.ToString())));
        }
        catch
        {
            // 저장 실패는 무시 - 다음 실행 때 시스템 언어로 다시 추정될 뿐
        }
    }
}
