namespace MigamBridge.Tray;

public enum AppLanguage
{
    Korean,
    English,
    Japanese
}

/// <summary>
/// 소규모 UI 문자열을 위한 직접 관리 번역 테이블.
/// .resx 리소스 방식 대신 이 방식을 쓴 이유: 문자열 개수가 적고(트레이 메뉴/팝업 정도),
/// 코드 파일 하나로 다루는 게 리소스 디자이너 없이도 손으로 바로 수정/추가하기 쉬움.
/// 문자열이 많이 늘어나면 .resx(다국어 리소스 파일 + ResourceManager) 방식으로 이관하는 걸 권장.
/// </summary>
internal static class Strings
{
    public static AppLanguage Current { get; private set; } = AppLanguage.Korean;

    /// <summary>언어가 바뀔 때 발생 - 열려있는 폼/메뉴가 텍스트를 다시 그리는 데 사용.</summary>
    public static event Action? LanguageChanged;

    public static void SetLanguage(AppLanguage language)
    {
        if (Current == language) return;
        Current = language;
        LanguageChanged?.Invoke();
    }

    /// <summary>시스템 UI 언어로부터 초기 기본값을 추정 (한국어/일본어 외에는 영어).</summary>
    public static AppLanguage DetectSystemDefault()
    {
        var iso = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return iso switch
        {
            "ko" => AppLanguage.Korean,
            "ja" => AppLanguage.Japanese,
            _ => AppLanguage.English
        };
    }

    private static readonly Dictionary<string, (string Ko, string En, string Ja)> Table = new()
    {
        ["tray.tooltip.none"] = ("Migam 측색기 브리지 (연결 없음)", "Migam Colorimeter Bridge (not connected)", "Migam 測色器ブリッジ(未接続)"),
        ["tray.tooltip.count"] = ("Migam 측색기 브리지 ({0}대 연결됨)", "Migam Colorimeter Bridge ({0} connected)", "Migam 測色器ブリッジ({0}台接続済み)"),

        ["menu.devices"] = ("Devices", "Devices", "デバイス"),
        ["menu.language"] = ("언어", "Language", "言語"),
        ["menu.exit"] = ("종료", "Exit", "終了"),

        ["devices.title"] = ("Migam 측색기", "Migam Colorimeter", "Migam 測色器"),
        ["devices.hint.default"] = ("기기를 더블클릭하면 연결/해제됩니다.", "Double-click a device to connect or disconnect.", "デバイスをダブルクリックすると接続・切断します。"),
        ["devices.hint.searching"] = ("검색 중...", "Searching...", "検索中..."),
        ["devices.hint.xriteError"] = ("X-Rite 조회 오류: {0}", "X-Rite query error: {0}", "X-Rite 照会エラー: {0}"),
        ["devices.hint.nixError"] = ("Nix 검색 오류: {0}", "Nix search error: {0}", "Nix 検索エラー: {0}"),
        ["devices.col.device"] = ("기기", "Device", "デバイス"),
        ["devices.col.mac"] = ("MAC / ID", "MAC / ID", "MAC / ID"),
        ["devices.col.status"] = ("상태", "Status", "状態"),
        ["devices.btn.refresh"] = ("다시 검색", "Rescan", "再検索"),

        ["status.connected"] = ("연결됨", "Connected", "接続済み"),
        ["status.connecting"] = ("연결 중...", "Connecting...", "接続中..."),
        ["status.disconnecting"] = ("해제 중...", "Disconnecting...", "切断中..."),
        ["status.disconnected"] = ("연결 안됨", "Not connected", "未接続"),

        ["menu.version"] = ("버전 {0}", "Version {0}", "バージョン {0}"),
        ["menu.checkUpdate"] = ("업데이트 확인", "Check for updates", "アップデートを確認"),
        ["menu.openLogFolder"] = ("로그 폴더 열기", "Open log folder", "ログフォルダを開く"),
        ["update.checking"] = ("업데이트 확인 중...", "Checking for updates...", "アップデートを確認中..."),
        ["update.installing"] = ("새 버전을 설치합니다. 잠시 후 자동으로 다시 시작됩니다.", "Installing update. The app will restart shortly.", "アップデートをインストールします。まもなく自動的に再起動します。"),
        ["update.upToDate"] = ("이미 최신 버전입니다.", "You're already on the latest version.", "すでに最新バージョンです。"),
        ["update.notConfigured"] = ("업데이트 서버가 아직 설정되지 않았습니다.", "Update server isn't configured yet.", "アップデートサーバーがまだ設定されていません。"),
        ["update.checkFailed"] = ("업데이트 확인에 실패했습니다. 네트워크를 확인해주세요.", "Couldn't check for updates. Please check your network.", "アップデートの確認に失敗しました。ネットワークをご確認ください。"),
        ["update.installFailed"] = ("업데이트 다운로드/설치에 실패했습니다.", "Failed to download or install the update.", "アップデートのダウンロード・インストールに失敗しました。"),
    };

    /// <summary>키에 해당하는 현재 언어 문자열을 반환. args가 있으면 string.Format 적용.</summary>
    public static string T(string key, params object[] args)
    {
        if (!Table.TryGetValue(key, out var entry))
            return key; // 키가 없으면 키 자체를 보여줘서 누락을 바로 눈치챌 수 있게 함

        var template = Current switch
        {
            AppLanguage.Korean => entry.Ko,
            AppLanguage.Japanese => entry.Ja,
            _ => entry.En
        };

        return args.Length > 0 ? string.Format(template, args) : template;
    }
}
