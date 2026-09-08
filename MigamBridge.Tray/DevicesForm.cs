using MigamBridge.Drivers;
using MigamBridge.Models;

namespace MigamBridge.Tray;

/// <summary>
/// 트레이 아이콘을 클릭하면 뜨는 작은 팝업 창.
/// - X-Rite(eXact)는 스캔이 필요없는 고정 후보라 즉시 한 줄 표시.
/// - Nix는 실제 BLE 스캔이 필요해서, NixDriver.DeviceDiscovered 이벤트가 발생할 때마다
///   "찾는 대로 하나씩" 리스트에 추가된다.
/// - 더블클릭: 연결 안됨 → 연결 시도 / 연결됨 → 해제 시도 (토글).
/// - 닫기(X) 버튼은 창을 숨기기만 함 - 트레이 앱 자체는 계속 백그라운드에서 상주.
/// - MAC 열: Nix SDK는 별도의 MAC 주소 필드가 없고, 블루투스 기기에서는 IDeviceCompat.Id 자체가
///   사실상 MAC 주소 역할을 함(공식 문서 확인됨). 그래서 우리 내부 device_id("nix-xxx")에서
///   접두사를 뗀 원본 Id를 그대로 MAC 열에 보여준다. X-Rite는 후보가 1개뿐이라 "-"로 표시.
/// - 연결/해제 실패 시 별도 팝업(MessageBox) 없이, 상태 열만 원래 상태로 조용히 되돌아간다.
/// - 다국어: Strings.T(key)로 모든 문구를 가져오고, Strings.LanguageChanged 이벤트를 구독해서
///   언어가 바뀌면 제목/열 헤더/버튼/각 행의 상태 텍스트를 다시 그린다.
/// </summary>
public class DevicesForm : Form
{
    private readonly NixDriver _nix;
    private readonly XriteDriver _xrite;
    private readonly ListView _list;
    private readonly Label _hint;
    private readonly Button _refreshButton;
    private readonly Dictionary<string, ListViewItem> _rowsByDeviceId = new();

    public DevicesForm(NixDriver nix, XriteDriver xrite)
    {
        _nix = nix;
        _xrite = xrite;

        Width = 400;
        Height = 360;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;

        _hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 32,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 8, 0)
        };

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false
        };
        _list.Columns.Add(string.Empty, 160); // 텍스트는 ApplyLanguage()가 채움
        _list.Columns.Add(string.Empty, 140);
        _list.Columns.Add(string.Empty, 80);
        _list.MouseDoubleClick += List_MouseDoubleClick;

        _refreshButton = new Button { Dock = DockStyle.Bottom, Height = 32 };
        _refreshButton.Click += async (_, _) => await RefreshAsync();

        Controls.Add(_list);
        Controls.Add(_hint);
        Controls.Add(_refreshButton);

        _nix.DeviceDiscovered += OnDeviceDiscovered;
        _xrite.DeviceDiscovered += OnDeviceDiscovered;
        _nix.ConnectionChanged += OnConnectionChanged;
        _xrite.ConnectionChanged += OnConnectionChanged;
        _nix.DeviceRemoved += OnDeviceRemoved;
        _xrite.DeviceRemoved += OnDeviceRemoved;
        Strings.LanguageChanged += OnLanguageChanged;

        ApplyLanguage();

        Shown += async (_, _) => await RefreshAsync();

        // 완전히 닫지 않고 숨기기만 함 - 트레이 앱은 계속 상주해야 함
        FormClosing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
        };
    }

    private void OnLanguageChanged()
    {
        if (InvokeRequired) { BeginInvoke(ApplyLanguage); return; }
        ApplyLanguage();
    }

    private void ApplyLanguage()
    {
        Text = Strings.T("devices.title");
        _hint.Text = Strings.T("devices.hint.default");
        _list.Columns[0].Text = Strings.T("devices.col.device");
        _list.Columns[1].Text = Strings.T("devices.col.mac");
        _list.Columns[2].Text = Strings.T("devices.col.status");
        _refreshButton.Text = Strings.T("devices.btn.refresh");

        // 기존 행들의 상태 텍스트도 현재 언어로 다시 그림 (기기명/MAC은 언어와 무관해서 그대로 둠)
        foreach (var row in _rowsByDeviceId.Values)
        {
            if (row.Tag is DeviceInfo info)
                row.SubItems[2].Text = StatusText(info.Status);
        }
    }

    private async Task RefreshAsync()
    {
        _list.Items.Clear();
        _rowsByDeviceId.Clear();
        _hint.Text = Strings.T("devices.hint.searching");

        // 이미 연결된 기기는 재스캔 결과와 상관없이 먼저 보여준다.
        // (BLE 기기는 연결된 상태에서는 광고(advertise)를 멈추는 경우가 많아서, 새로 스캔해도
        // 이미 연결된 기기가 다시 안 잡혀서 "다시 검색"을 누르면 목록에서 사라지는 문제가 있었음)
        foreach (var info in _nix.GetConnectedDeviceInfos())
            UpsertRow(info);
        foreach (var info in _xrite.GetConnectedDeviceInfos())
            UpsertRow(info);

        try
        {
            // X-Rite는 스캔이 필요없는 고정 후보 - ListDevicesAsync 호출 자체가 DeviceDiscovered를 즉시 발생시킴
            await _xrite.ListDevicesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _hint.Text = Strings.T("devices.hint.xriteError", ex.Message);
        }

        // Nix는 실제 BLE 스캔 - 완료까지 기다리지 않고 백그라운드로 돌리고,
        // 발견되는 대로 DeviceDiscovered 이벤트를 통해 하나씩 리스트에 추가된다.
        _ = Task.Run(async () =>
        {
            try
            {
                await _nix.ListDevicesAsync(CancellationToken.None);
                BeginInvoke(() => _hint.Text = Strings.T("devices.hint.default"));
            }
            catch (Exception ex)
            {
                BeginInvoke(() => _hint.Text = Strings.T("devices.hint.nixError", ex.Message));
            }
        });
    }

    private void OnDeviceDiscovered(DeviceInfo info)
    {
        if (InvokeRequired) { BeginInvoke(() => OnDeviceDiscovered(info)); return; }
        UpsertRow(info);
    }

    private void OnConnectionChanged(DeviceInfo info)
    {
        if (InvokeRequired) { BeginInvoke(() => OnConnectionChanged(info)); return; }
        UpsertRow(info);
    }

    /// <summary>
    /// 전원이 꺼지는 등 예기치 않은 연결 끊김이 감지되면 발생 - 행을 완전히 목록에서 제거한다.
    /// (참고: X-Rite는 SDK에 실제 "기기 존재 여부" 확인 API가 없어서, 이후 "다시 검색"을 누르면
    /// 전원이 계속 꺼져있어도 고정 후보 행이 다시 나타난다 - 연결을 시도해봐야만 꺼진 걸 알 수 있는
    /// SDK 자체의 한계. Nix는 실제 BLE 스캔 기반이라 전원이 꺼져있으면 재검색해도 다시 안 나타난다.)
    /// </summary>
    private void OnDeviceRemoved(string deviceId)
    {
        if (InvokeRequired) { BeginInvoke(() => OnDeviceRemoved(deviceId)); return; }

        if (_rowsByDeviceId.TryGetValue(deviceId, out var row))
        {
            _list.Items.Remove(row);
            _rowsByDeviceId.Remove(deviceId);
        }
    }

    private void UpsertRow(DeviceInfo info)
    {
        if (IsDisposed) return;

        var mac = ExtractMacOrId(info);

        if (_rowsByDeviceId.TryGetValue(info.Id, out var row))
        {
            row.SubItems[1].Text = mac;
            row.SubItems[2].Text = StatusText(info.Status);
            row.Tag = info;
        }
        else
        {
            var newRow = new ListViewItem(new[] { $"{info.Model} ({info.Vendor})", mac, StatusText(info.Status) })
            {
                Tag = info
            };
            _list.Items.Add(newRow);
            _rowsByDeviceId[info.Id] = newRow;
        }
    }

    /// <summary>
    /// nix-xxx 형태의 내부 device_id에서 접두사를 떼면 그게 곧 Nix SDK의 원본 Id이고,
    /// 블루투스 기기에서는 이 값 자체가 MAC 주소다(Nix SDK 공식 문서 확인).
    /// X-Rite는 항상 후보가 1개뿐이라 구분할 필요가 없어 "-"로 표시.
    /// </summary>
    private static string ExtractMacOrId(DeviceInfo info)
    {
        if (string.Equals(info.Vendor, "nix", StringComparison.OrdinalIgnoreCase)
            && info.Id.StartsWith("nix-", StringComparison.OrdinalIgnoreCase))
        {
            return info.Id["nix-".Length..];
        }
        return "-";
    }

    private async void List_MouseDoubleClick(object? sender, MouseEventArgs e)
    {
        var hit = _list.HitTest(e.Location);
        if (hit.Item is null) return;

        var info = (DeviceInfo)hit.Item.Tag!;

        // 이미 연결됨 → 더블클릭하면 해제 (토글)
        if (info.Status == MigamBridge.Models.DeviceStatus.Connected)
        {
            hit.Item.SubItems[2].Text = Strings.T("status.disconnecting");
            try
            {
                IColorimeterDriver driver = string.Equals(info.Vendor, "nix", StringComparison.OrdinalIgnoreCase) ? _nix : _xrite;
                await driver.DisconnectAsync(info.Id, CancellationToken.None);
                // 성공 시 상태 텍스트는 DisconnectAsync가 발생시키는 ConnectionChanged 이벤트로 갱신됨
            }
            catch
            {
                // 해제 실패도 조용히 - 다이얼로그 없이 실제 상태를 그대로 반영
                if (!IsDisposed)
                    hit.Item.SubItems[2].Text = StatusText(info.Status);
            }
            return;
        }

        if (info.Status == MigamBridge.Models.DeviceStatus.Connecting) return; // 연결 시도 중엔 중복 클릭 무시

        // 연결 안됨 → 더블클릭하면 연결
        hit.Item.SubItems[2].Text = Strings.T("status.connecting");
        try
        {
            await BridgeConnector.ConnectAsync(_nix, _xrite, info.Id, CancellationToken.None);
        }
        catch
        {
            if (!IsDisposed)
                hit.Item.SubItems[2].Text = Strings.T("status.disconnected");
        }
    }

    private static string StatusText(DeviceStatus status) => status switch
    {
        DeviceStatus.Connected => Strings.T("status.connected"),
        DeviceStatus.Connecting => Strings.T("status.connecting"),
        _ => Strings.T("status.disconnected") // Disconnected, Error 모두 동일하게 표시
    };
}
