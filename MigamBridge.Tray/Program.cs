namespace MigamBridge.Tray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // 이미 실행 중이면 중복 실행 방지 (로그온 자동시작 + 사용자가 직접 실행 둘 다 대비)
        using var singleInstanceMutex = new Mutex(true, "MigamBridgeTray_SingleInstance_Mutex", out var isNew);
        if (!isNew)
        {
            MessageBox.Show("Migam 측색기 브리지가 이미 실행 중입니다. (우측 하단 트레이 아이콘 확인)",
                "Migam", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayAppContext());
    }
}
