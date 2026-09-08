using System.Text;

namespace MigamBridge;

/// <summary>
/// 간단한 파일 로거.
///
/// 트레이 앱(MigamBridge.Tray)은 WinExe 서브시스템이라 콘솔 창이 없고, Console.WriteLine을 해도
/// 어디에도 출력되지 않고 그냥 사라진다. 그래서 진단용 로그(X-Rite/Nix 드라이버의 연결 시도 상세
/// 내역 등)를 파일에도 같이 남겨서, 문제가 생겼을 때 이 파일을 열어보면 원인을 확인할 수 있게 한다.
///
/// 로그 파일 위치: %AppData%\Migam\logs\bridge-yyyyMMdd.log (날짜별로 새 파일)
/// </summary>
public static class Logger
{
    private static readonly object Lock = new();

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Migam", "logs");

    public static string CurrentLogFilePath =>
        Path.Combine(LogDirectory, $"bridge-{DateTime.Now:yyyyMMdd}.log");

    public static void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";

        // 콘솔이 있는 환경(예: MigamBridge 단독 실행, 디버깅)에서는 여전히 콘솔에도 보이도록 유지
        try { Console.WriteLine(line); } catch { /* 콘솔이 없으면 예외 없이 그냥 무시됨 */ }

        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(CurrentLogFilePath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // 로그 기록 실패는 무시 - 로깅 문제로 실제 기능(연결/측정)이 막히면 안 됨
        }
    }
}
