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
    private readonly ToolStripMenuItem _openLogFolderMenuItem;
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

        _openLogFolderMenuItem = new ToolStripMenuItem();
        _openLogFolderMenuItem.Click += (_, _) => OpenLogFolder();

        _exitMenuItem = new ToolStripMenuItem();
        _exitMenuItem.Click += (_, _) => _ = ExitApplicationAsync();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_devicesMenuItem);
        menu.Items.Add(_languageMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_versionMenuItem);
        menu.Items.Add(_checkUpdateMenuItem);
        menu.Items.Add(_openLogFolderMenuItem);
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
        _openLogFolderMenuItem.Text = Strings.T("menu.openLogFolder");
        _exitMenuItem.Text = Strings.T("menu.exit");
    }

    /// <summary>진단 로그가 쌓이는 폴더(%AppData%\Migam\logs)를 탐색기로 열어준다.</summary>
    private static void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(Logger.LogDirectory); // 아직 로그가 한 번도 안 쌓였으면 폴더가 없을 수 있음
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Logger.LogDirectory,
                UseShellExecute = true
            });
        }
        catch { /* 탐색기 실행 실패는 무시 - 치명적이지 않음 */ }
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
            UpdateCheckResult result;
            try
            {
                result = await UpdateChecker.CheckAndInstallIfNewerAsync(manifestUrl, CancellationToken.None);
            }
            catch
            {
                result = UpdateCheckResult.CheckFailed;
            }

            if (result == UpdateCheckResult.Installing)
            {
                _trayIcon.ShowBalloonTip(4000, "Migam", Strings.T("update.installing"), ToolTipIcon.Info);
                await Task.Delay(1500); // 풍선 알림이 잠깐 눈에 띄도록 약간의 여유를 둠
                await ExitApplicationAsync();
                return;
            }

            // 자동 주기 확인(manual=false)에서는 새 버전이 없으면 조용히 끝냄.
            // 수동 확인(manual=true)일 때만 상황에 맞는 결과를 정확히 알려준다.
            if (manual)
            {
                var messageKey = result switch
                {
                    UpdateCheckResult.UpToDate => "update.upToDate",
                    UpdateCheckResult.NotConfigured => "update.notConfigured",
                    UpdateCheckResult.CheckFailed => "update.checkFailed",
                    UpdateCheckResult.DownloadOrInstallFailed => "update.installFailed",
                    _ => "update.upToDate"
                };
                _trayIcon.ShowBalloonTip(3000, "Migam", Strings.T(messageKey), ToolTipIcon.Info);
            }
        }
        finally
        {
            _updateCheckInProgress = false;
        }
    }

    private async Task ExitApplicationAsync()
    {
        _updateTimer.Dispose();

        // 연결된 기기를 먼저 정상적으로 해제 시도 (전원 꺼짐이 아니라 우리가 스스로 종료하는 것이므로
        // NixDriver.Dispose()에만 맡기지 않고 명시적으로 DisconnectAsync를 거쳐 이벤트/자원 정리를 깔끔히 함).
        // 단, eXact.dll의 Disconnect()가 통신 상태에 따라 오래 걸리거나 멈출 수 있다는 게 최근에
        // USB 연결 시도에서 확인됐기 때문에(Connection Timeout 로그 참고), 각 단계마다 최대 3초까지만
        // 기다리고 그 이후엔 그냥 다음 단계로 넘어간다 - "정리가 안 끝나서 종료 자체가 멈추는" 상황 방지.
        await RunWithTimeout(async () =>
        {
            if (_xrite.IsConnected)
                await _xrite.DisconnectAsync("xrite-exact1", CancellationToken.None);
        });

        await RunWithTimeout(async () =>
        {
            foreach (var id in _nix.ConnectedDeviceIds.ToList())
                await _nix.DisconnectAsync(id, CancellationToken.None);
        });

        await RunWithTimeout(() => _webApp.StopAsync());
        await RunWithTimeout(() => _webApp.DisposeAsync().AsTask());

        // 정리가 끝난(또는 타임아웃으로 포기한) 뒤에 아이콘을 지운다 - "아이콘은 사라졌는데
        // 프로세스는 계속 떠있는" 것처럼 보이는 상황을 막기 위해 아이콘 숨김을 맨 마지막으로 옮김.
        _trayIcon.Visible = false;

        // Application.Exit()이 메시지 루프를 정상적으로 끝내지 못하는 경우(예: 어딘가에 여전히
        // 살아있는 백그라운드 스레드/네이티브 핸들이 있는 경우)에 대비해, 위 정리를 이미 다 시도한
        // 뒤이므로 최종적으로 프로세스 자체를 강제 종료해서 "종료했는데 프로세스가 안 없어지는"
        // 문제가 재발하지 않도록 보장한다.
        Application.Exit();
        Environment.Exit(0);
    }

    /// <summary>주어진 작업을 최대 timeoutMs까지만 기다린다. 실패/타임아웃 모두 조용히 무시 - 종료 처리를
    /// 절대 막으면 안 되므로, 정리가 실패해도 다음 단계(또는 최종 강제 종료)로 넘어가는 게 우선이다.</summary>
    private static async Task RunWithTimeout(Func<Task> action, int timeoutMs = 3000)
    {
        try
        {
            var task = action();
            await Task.WhenAny(task, Task.Delay(timeoutMs));
        }
        catch { /* 무시 */ }
    }
}
