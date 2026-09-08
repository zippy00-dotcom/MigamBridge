using MigamBridge;
using MigamBridge.Drivers;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace MigamBridge.Tray;

/// <summary>
/// 트레이 상주 앱의 최상위 컨텍스트.
///
/// - BridgeHost.BuildApp()으로 기존 ASP.NET Core API(포트 47990)를 이 프로세스 안에서 그대로 띄운다.
///   (웹 콘솔은 지금까지처럼 http://localhost:47990/v1/... 을 그대로 호출하면 됨 - 변경 없음)
/// - NotifyIcon(트레이 아이콘)을 만들고, 좌클릭하면 DevicesForm 팝업을 띄운다.
/// - 시작 시 %AppData%\Migam\lastdevices.json에 저장된 마지막 기기로 자동 재연결을 시도한다
///   - X-Rite는 즉시 ConnectAsync, Nix는 QuickConnectAsync로 짧게 스캔.
/// - 하나라도 연결되면 트레이 아이콘이 회색 → 파란색으로 바뀐다.
/// - 트레이 우클릭 메뉴에 "언어" 서브메뉴(한국어/English/日本語)를 제공하고,
///   선택은 %AppData%\Migam\settings.json에 저장되어 다음 실행 때도 유지된다.
/// - 버전 확인 + 완전 자동 업데이트: 시작 시 1회 + 이후 6시간마다 Update:ManifestUrl을 확인하고,
///   새 버전이 있으면 조용히 다운로드/검증/설치 후 스스로 재시작한다. 메뉴에서 수동으로도 확인 가능.
/// - 종료 시(트레이 메뉴 "종료") 연결된 기기를 먼저 정상적으로 해제한 뒤 프로세스를 정리한다.
/// </summary>
public class TrayAppContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly DevicesForm _devicesForm;
    private readonly NixDriver _nix;
    private readonly XriteDriver _xrite;
    private readonly WebApplication _webApp;
    private readonly System.Threading.Timer _updateTimer;

    private readonly ToolStripMenuItem _devicesMenuItem;
    private readonly ToolStripMenuItem _languageMenuItem;
    private readonly ToolStripMenuItem _languageKoItem;
    private readonly ToolStripMenuItem _languageEnItem;
    private readonly ToolStripMenuItem _languageJaItem;
    private readonly ToolStripMenuItem _versionMenuItem;
    private readonly ToolStripMenuItem _checkUpdateMenuItem;
    private readonly ToolStripMenuItem _exitMenuItem;

    private bool _updateCheckInProgress;

    public TrayAppContext()
    {
        // 0) 저장된 언어 설정 로드 (없으면 시스템 언어로 추정)
        Strings.SetLanguage(AppSettingsStore.LoadLanguage());

        // 1) 브리지 API를 인프로세스로 백그라운드 시작 - Kestrel과 WinForms 메시지 루프가 같은 프로세스에서 동시에 돈다.
        _webApp = BridgeHost.BuildApp(Array.Empty<string>());
        _nix = _webApp.Services.GetRequiredService<NixDriver>();
        _xrite = _webApp.Services.GetRequiredService<XriteDriver>();
        _ = _webApp.RunAsync("http://127.0.0.1:47990");

        // 2) Devices 팝업 창 (숨김 상태로 미리 생성해둠 - 트레이 클릭 시 바로 뜨도록)
        _devicesForm = new DevicesForm(_nix, _xrite);

        // 3) 트레이 컨텍스트 메뉴 (텍스트는 Opening 이벤트에서 매번 현재 언어로 갱신)
        _devicesMenuItem = new ToolStripMenuItem();
        _devicesMenuItem.Click += (_, _) => ShowDevicesForm();

        _languageKoItem = new ToolStripMenuItem("한국어") { Tag = AppLanguage.Korean };
        _languageEnItem = new ToolStripMenuItem("English") { Tag = AppLanguage.English };
        _languageJaItem = new ToolStripMenuItem("日本語") { Tag = AppLanguage.Japanese };
        foreach (var item in new[] { _languageKoItem, _languageEnItem, _languageJaItem })
        {
            item.Click += (s, _) =>
            {
                var lang = (AppLanguage)((ToolStripMenuItem)s!).Tag!;
                Strings.SetLanguage(lang);
                AppSettingsStore.SaveLanguage(lang);
                UpdateIconAndPersist(); // 트레이 툴팁 텍스트도 즉시 갱신
            };
        }

        _languageMenuItem = new ToolStripMenuItem();
        _languageMenuItem.DropDownItems.AddRange(new ToolStripItem[] { _languageKoItem, _languageEnItem, _languageJaItem });

        // 버전 표시(정보용, 클릭 불가) + 수동 업데이트 확인
        _versionMenuItem = new ToolStripMenuItem { Enabled = false };
        _checkUpdateMenuItem = new ToolStripMenuItem();
        _checkUpdateMenuItem.Click += (_, _) => _ = RunUpdateCheckAsync(manual: true);

        _exitMenuItem = new ToolStripMenuItem();
        _exitMenuItem.Click += (_, _) => _ = ExitApplicationAsync();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_devicesMenuItem);
        menu.Items.Add(_languageMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_versionMenuItem);
        menu.Items.Add(_checkUpdateMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_exitMenuItem);
        menu.Opening += (_, _) => RefreshMenuTexts();

        // 4) 트레이 아이콘
        _trayIcon = new NotifyIcon
        {
            Icon = IconFactory.CreateCircleIcon(IconFactory.GrayColor),
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.MouseClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Left)
                ShowDevicesForm();
        };

        // 5) 연결 상태 변화 → 아이콘 색상 갱신 + 마지막 연결 기기 저장
        _nix.ConnectionChanged += _ => UpdateIconAndPersist();
        _xrite.ConnectionChanged += _ => UpdateIconAndPersist();
        UpdateIconAndPersist(); // 초기 툴팁 텍스트 설정

        // 6) 시작 시 마지막 기기 자동 재연결 (백그라운드, UI를 막지 않음)
        _ = AutoReconnectAsync();

        // 7) 버전 자동 확인: 시작 30초 후 1회, 이후 6시간 간격으로 반복.
        //    Update:ManifestUrl이 비어있으면 UpdateChecker가 조용히 스킵한다.
        _updateTimer = new System.Threading.Timer(
            _ => _ = RunUpdateCheckAsync(manual: false),
            null,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromHours(6));
    }

    private void RefreshMenuTexts()
    {
        _devicesMenuItem.Text = Strings.T("menu.devices");
        _languageMenuItem.Text = Strings.T("menu.language");
        _languageKoItem.Checked = Strings.Current == AppLanguage.Korean;
        _languageEnItem.Checked = Strings.Current == AppLanguage.English;
        _languageJaItem.Checked = Strings.Current == AppLanguage.Japanese;
        _versionMenuItem.Text = Strings.T("menu.version", UpdateChecker.CurrentVersion);
        _checkUpdateMenuItem.Text = Strings.T("menu.checkUpdate");
        _checkUpdateMenuItem.Enabled = !_updateCheckInProgress;
        _exitMenuItem.Text = Strings.T("menu.exit");
    }

    private void ShowDevicesForm()
    {
        var pos = Cursor.Position;
        var screen = Screen.FromPoint(pos).WorkingArea;

        var x = Math.Min(pos.X, screen.Right - _devicesForm.Width);
        var y = Math.Min(pos.Y - _devicesForm.Height, screen.Bottom - _devicesForm.Height);
        _devicesForm.Location = new Point(Math.Max(x, screen.Left), Math.Max(y, screen.Top));

        _devicesForm.Show();
        _devicesForm.Activate();
    }

    private void UpdateIconAndPersist()
    {
        var connectedCount = (_nix.IsConnected ? 1 : 0) + (_xrite.IsConnected ? 1 : 0);

        _trayIcon.Icon = IconFactory.CreateCircleIcon(connectedCount > 0 ? IconFactory.BlueColor : IconFactory.GrayColor);
        _trayIcon.Text = connectedCount > 0
            ? Strings.T("tray.tooltip.count", connectedCount)
            : Strings.T("tray.tooltip.none");

        // 마지막 연결 기기 갱신 - 해제 시에는 지우지 않고 유지(다음 실행 때 같은 기기로 재시도하기 위함).
        var last = LastDeviceStore.Load();
        var nixId = _nix.ConnectedDeviceIds.FirstOrDefault() ?? last.NixDeviceId;
        var xriteId = _xrite.IsConnected ? "xrite-exact1" : last.XriteDeviceId;
        LastDeviceStore.Save(new LastDevices(nixId, xriteId));
    }

    private async Task AutoReconnectAsync()
    {
        var last = LastDeviceStore.Load();
        var tasks = new List<Task>();

        if (!string.IsNullOrEmpty(last.XriteDeviceId))
        {
            tasks.Add(Task.Run(async () =>
            {
                try { await _xrite.ConnectAsync(last.XriteDeviceId!, CancellationToken.None); }
                catch { /* 기기가 꺼져있거나 없으면 그냥 미연결 상태로 둠 - 사용자가 Devices에서 수동 연결 */ }
            }));
        }

        if (!string.IsNullOrEmpty(last.NixDeviceId))
        {
            var rawId = last.NixDeviceId!.StartsWith("nix-", StringComparison.OrdinalIgnoreCase)
                ? last.NixDeviceId["nix-".Length..]
                : last.NixDeviceId;

            tasks.Add(Task.Run(async () =>
            {
                try { await _nix.QuickConnectAsync(rawId, TimeSpan.FromSeconds(5), CancellationToken.None); }
                catch { /* 실패 시 미연결 상태로 둠 */ }
            }));
        }

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// 업데이트 확인. manual=true면 메뉴에서 수동으로 누른 것 - 결과를 풍선 알림으로 알려준다.
    /// 자동 주기 확인(manual=false)은 새 버전이 없으면 아무 알림 없이 조용히 끝난다.
    /// 새 버전을 찾아 설치를 시작한 경우, 잠깐의 알림 후 앱을 스스로 정리하고 종료한다
    /// (Setup.exe가 어차피 강제 종료시키긴 하지만, 기기 연결을 먼저 정상적으로 풀어주기 위함).
    /// </summary>
    private async Task RunUpdateCheckAsync(bool manual)
    {
        if (_updateCheckInProgress) return;
        _updateCheckInProgress = true;
        try
        {
            if (manual)
                _trayIcon.ShowBalloonTip(3000, "Migam", Strings.T("update.checking"), ToolTipIcon.Info);

            var manifestUrl = _webApp.Configuration["Update:ManifestUrl"];
            bool installing;
            try
            {
                installing = await UpdateChecker.CheckAndInstallIfNewerAsync(manifestUrl, CancellationToken.None);
            }
            catch
            {
                installing = false;
            }

            if (installing)
            {
                _trayIcon.ShowBalloonTip(4000, "Migam", Strings.T("update.installing"), ToolTipIcon.Info);
                await Task.Delay(1500); // 풍선 알림이 잠깐 눈에 띄도록 약간의 여유를 둠
                await ExitApplicationAsync();
                return;
            }

            if (manual)
                _trayIcon.ShowBalloonTip(3000, "Migam", Strings.T("update.upToDate"), ToolTipIcon.Info);
        }
        finally
        {
            _updateCheckInProgress = false;
        }
    }

    private async Task ExitApplicationAsync()
    {
        _updateTimer.Dispose();
        _trayIcon.Visible = false;

        // 연결된 기기를 먼저 정상적으로 해제 (전원 꺼짐이 아니라 우리가 스스로 종료하는 것이므로
        // NixDriver.Dispose()에만 맡기지 않고 명시적으로 DisconnectAsync를 거쳐 이벤트/자원 정리를 깔끔히 함)
        try
        {
            if (_xrite.IsConnected)
                await _xrite.DisconnectAsync("xrite-exact1", CancellationToken.None);
        }
        catch { /* 종료 중 오류는 무시 - 어차피 프로세스가 끝남 */ }

        try
        {
            foreach (var id in _nix.ConnectedDeviceIds.ToList())
                await _nix.DisconnectAsync(id, CancellationToken.None);
        }
        catch { /* 종료 중 오류는 무시 */ }

        try { await _webApp.StopAsync(); } catch { /* 무시 */ }
        try { await _webApp.DisposeAsync(); } catch { /* 무시 */ }

        Application.Exit();
    }
}
