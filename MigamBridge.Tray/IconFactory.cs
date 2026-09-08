namespace MigamBridge.Tray;

/// <summary>
/// 임시 로고: 실제 Migam 로고 파일이 준비되기 전까지, 실행 중 GDI+로 간단한
/// 원형 "M" 아이콘을 그려서 트레이 아이콘으로 사용한다.
///
/// [나중에 실제 로고로 교체하는 방법]
/// 1. Migam 로고를 회색 버전(연결 없음)/파란색 버전(1대 이상 연결) 두 개의 .ico로 준비
///    (예: Assets/tray_gray.ico, Assets/tray_blue.ico, 16x16~32x32 권장)
/// 2. csproj에 <None Include="Assets\*.ico"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></None> 추가
/// 3. 아래 CreateCircleIcon 호출부(TrayAppContext.cs)를
///    new Icon("Assets/tray_gray.ico") / new Icon("Assets/tray_blue.ico") 로 교체
/// 이 파일(IconFactory.cs)은 그대로 두거나 삭제해도 무방함.
/// </summary>
internal static class IconFactory
{
    public static readonly Color GrayColor = Color.FromArgb(255, 158, 158, 158);
    public static readonly Color BlueColor = Color.FromArgb(255, 33, 150, 243);

    public static Icon CreateCircleIcon(Color fillColor)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var brush = new SolidBrush(fillColor);
            g.FillEllipse(brush, 1, 1, 29, 29);

            using var font = new Font("Segoe UI", 14f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(Color.White);
            const string text = "M";
            var size = g.MeasureString(text, font);
            g.DrawString(text, font, textBrush, (32 - size.Width) / 2f, (32 - size.Height) / 2f - 1f);
        }

        // 주의: Icon.FromHandle로 만든 아이콘은 내부적으로 네이티브 핸들을 물고 있음.
        // 트레이 아이콘은 앱 종료까지 계속 쓰이므로 여기서는 명시적 해제를 생략함(임시 로고 특성상 실용적 타협).
        // 실제 로고 파일 방식으로 바꾸면 이 이슈 자체가 없어짐.
        var hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }
}
