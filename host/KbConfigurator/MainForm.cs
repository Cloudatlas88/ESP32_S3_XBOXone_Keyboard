using KbConfigurator.Hid;
using KbConfigurator.Protocol;
using KbConfigurator.Ui;
using KbConfigurator.Views;

namespace KbConfigurator;

/// <summary>
/// 主窗体：设备条 + 配置条 + 三页签 + 状态栏。
///
/// ★ 布局原则（本次改造的重点）：
///   全部用 Dock / TableLayoutPanel / FlowLayoutPanel，**不写 Location 坐标**。
///   改造前这里有 41 处硬编码坐标 + 写死的窗口尺寸，导致窗口一缩放控件就不跟随、
///   高 DPI 下会错位，而且每加一个控件都要手算位置。
///
/// ★ 语义分组（原来的"工具栏"把两类东西混在一起了）：
///   · 设备条 —— 跟"设备连接"有关：连接/断开、查看接口、重启设备
///   · 配置条 —— 跟"配置读写"有关：从设备读取、保存到设备、恢复默认
///   · 「重置摇杆校准」不在这两条里，它属于设备操作且必须在摇杆数据旁边，
///     已移到「输入调试」页（那里能看到行程刻度，才知道校准有没有收敛）
/// </summary>
public sealed class MainForm : Form
{
    private readonly KbDevice _dev = new();
    private readonly ConfigSession _session = new();

    // ── 设备条（只保留硬件信息，不放任何功能按钮）──
    private readonly Label  _lblDevice  = new() { AutoSize = true, Margin = new Padding(0, 4, 0, 0) };

    // ── 配置条 ──
    // 顺序按需求定：从设备读取 → 连接/断开 → 保存并重启 → 恢复默认
    private readonly Button _btnRead    = L.Button("从设备读取", 110);
    private readonly Button _btnConnect = L.Button("连接设备", 92);
    private readonly Button _btnSave    = L.Button("保存并重启", 110, DarkTheme.Accent);
    private readonly Button _btnDefault = L.Button("恢复默认", 96);
    private readonly Label  _lblDirty   = new() { AutoSize = true, Margin = new Padding(14, 6, 0, 0) };

    // ── 页签 ──
    // ★ 用 NoArrowTabControl：摇杆映射成的就是方向键，原生控件会被方向键切页签
    private readonly NoArrowTabControl _tabs = new() { Dock = DockStyle.Fill };

    private readonly ButtonLayoutPanel _layout = new();
    private readonly MacroPanel  _macro  = new();

    // ── 状态栏 ──
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _statusText = new("就绪");
    private readonly ToolStripStatusLabel _statusRight = new("");

    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 1500 };

    private bool _userDisconnected;
    private bool _rebooting;
    private DateTime _rebootStartedAt;
    private byte[]? _expectAfterReboot;
    private byte[]? _lastSavedBytes;

    /// <summary>
    /// 测试接缝：为 true 时**完全不碰设备**（不枚举、不连接、不轮询）。
    ///
    /// 给 <c>--layout</c> 布局验收用 —— 布局检查不应该依赖真实设备，
    /// 否则会去连 USB、读配置，甚至因为异常弹出模态框把测试卡死。
    /// 只有布局验证会用，正常运行永远是 false。
    /// </summary>
    internal static bool Headless;

    public MainForm()
    {
        Text = "S3_Keyboard  Ver001   —— ESP32-S3 手柄转 USB HID 键盘";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 640);          // 只设下限，上限交给用户
        Size = new Size(1180, 860);
        Font = DarkTheme.Ui(9f);
        BackColor = DarkTheme.FormBack;
        ForeColor = DarkTheme.Text;
        AutoScaleMode = AutoScaleMode.Font;        // 配合布局容器才能真正跟随 DPI/字体

        Controls.Add(BuildTabArea());
        Controls.Add(BuildConfigBar());
        Controls.Add(BuildDeviceBar());
        Controls.Add(BuildStatusBar());

        // 共享配置模型
        _session.Register(_macro);
        _session.Register(_layout);      // 只读显示；编辑走 SlotMappingChanged（见该页注释）

        HookEvents();

        DarkTheme.HookAllTabs(this);
        DarkTheme.Apply(this);

        Load += (_, _) => PollDevice();
    }

    // ══════════════════════════════════════════════════════════════
    //  界面构建
    // ══════════════════════════════════════════════════════════════

    private Control BuildDeviceBar()
    {
        // ★ 这一行【只放硬件信息】，不放任何按钮。
        //   原来这里有 [连接设备] [设备▾]（下拉含 查看接口/重启设备/断开）——
        //   按需求全部去掉：重启并入了「保存并重启」，断开移到配置条。
        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 1,
            Padding = new Padding(14, 8, 14, 2),
            BackColor = DarkTheme.PanelBack,
        };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _lblDevice.Anchor = AnchorStyles.Left;
        bar.Controls.Add(_lblDevice, 0, 0);
        return bar;
    }

    private Control BuildConfigBar()
    {
        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 5,
            RowCount = 1,
            Padding = new Padding(14, 4, 14, 6),
            BackColor = DarkTheme.FormBack,
        };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));   // 从设备读取
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));   // 连接/断开
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));   // 保存并重启
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));   // 恢复默认
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));    // 未保存提示

        L.Cell(bar, _btnRead, 0, 0);
        L.Cell(bar, _btnConnect, 1, 0);
        L.Cell(bar, _btnSave, 2, 0);
        L.Cell(bar, _btnDefault, 3, 0);
        L.Cell(bar, _lblDirty, 4, 0);

        return bar;
    }

    private Control BuildTabArea()
    {
        // 两页定型（设计文档 §七 步骤 5）：
        //   旧「输入调试」和「按键映射」已合并进「按键布局」——
        //   调试读数在高亮图上直接看，映射在热区上直接改。
        //   旧两页的功能一个都没丢：读数条 / 校准按钮 / 死区反向 /
        //   启用开关 / 宏绑定提示 / 原始电平全都搬过去了。
        var tpLayout = new TabPage("按键布局");
        var tpMacro  = new TabPage("宏编辑");

        tpLayout.Controls.Add(_layout);
        tpMacro.Controls.Add(_macro);

        _tabs.TabPages.Add(tpLayout);
        _tabs.TabPages.Add(tpMacro);

        return _tabs;
    }

    private Control BuildStatusBar()
    {
        _statusText.Spring = true;
        _statusText.TextAlign = ContentAlignment.MiddleLeft;
        _statusRight.TextAlign = ContentAlignment.MiddleRight;

        _status.Items.Add(_statusText);
        _status.Items.Add(_statusRight);
        _status.SizingGrip = false;

        return _status;
    }

    private void HookEvents()
    {
        _dev.InputReceived += OnInputReceived;
        _dev.Message       += m => SetStatus(m);

        _btnConnect.Click += (_, _) =>
        {
            if (_dev.IsOpen) { _userDisconnected = true; Disconnect("已断开（手动）"); }
            else Connect();
        };
        _btnRead.Click    += (_, _) => DoRead();
        _btnSave.Click    += (_, _) => DoSaveAndReboot();
        _btnDefault.Click += (_, _) => DoDefault();

        _layout.RequestCalibration += DoCalibReset;   // 重置摇杆校准按设计挪到「按键布局」页
        _layout.SlotMappingChanged += OnSlotMappingChanged;
        _layout.SlotEnabledChanged += OnSlotEnabledChanged;
        _layout.StickSettingsChanged += OnStickSettingsChanged;

        _poll.Tick += (_, _) => { PollDevice(); CheckDirty(); };
        if (!Headless) _poll.Start();

        // 快捷键：Ctrl+S = 保存并重启，Ctrl+R = 读取，F5 = 重新检测设备
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.S) { DoSaveAndReboot(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.R) { DoRead(); e.Handled = true; }
            else if (e.KeyCode == Keys.F5) { PollDevice(); e.Handled = true; }
        };

        FormClosing += (_, e) =>
        {
            // 有未保存改动时提醒（P6）—— 以前直接关就把改动丢了，没有任何提示
            if (_dirty && Connected)
            {
                var r = MessageBox.Show(
                    "界面上还有没保存到设备的改动。\n\n【是】保存并重启后退出\n【否】不保存直接退出\n【取消】回去继续编辑",
                    "有未保存的改动", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);

                if (r == DialogResult.Cancel) { e.Cancel = true; return; }
                if (r == DialogResult.Yes && !DoSave()) { e.Cancel = true; return; }
            }

            _poll.Stop();
            _dev.Dispose();
        };
    }

    private bool Connected => _dev is { IsOpen: true };

    // ══════════════════════════════════════════════════════════════
    //  未保存提示
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 未保存改动检测。
    ///
    /// ★ 用"序列化后逐字节比对"而不是给每个输入控件挂 Changed 事件：
    ///   两个面板加起来约 50 个 ComboBox/CheckBox/NumericUpDown，
    ///   一个个挂事件既啰嗦又容易漏（漏一个就是"改了却不提示"）。
    ///   直接比对整份配置，任何改动都跑不掉，而且天然幂等。
    ///
    ///   比对基线取「读取/保存之后规范化过的字节」，这样刚从设备读回来不会
    ///   因为"界面把宏计数规范化了一下"就误报有改动。
    /// </summary>
    private bool _dirty;

    private void CheckDirty()
    {
        if (!Connected || _lastSavedBytes is null) { SetDirty(false); return; }

        _session.CollectFromUi();
        SetDirty(!_session.Config.ToBytes().SequenceEqual(_lastSavedBytes));
    }

    private void SetDirty(bool d)
    {
        if (_dirty == d) return;
        _dirty = d;
        _lblDirty.Text = d ? "● 有未保存改动" : "○ 已与设备同步";
        _lblDirty.ForeColor = d ? DarkTheme.Warn : DarkTheme.TextDim;
    }

    // ══════════════════════════════════════════════════════════════
    //  设备连接
    // ══════════════════════════════════════════════════════════════

    private void PollDevice()
    {
        if (Headless) return;      // 布局验收模式：完全不碰设备

        var vendor = KbDevice.FindVendorCollection(KbDevice.Enumerate());

        if (vendor is null)
        {
            if (_rebooting)
            {
                if ((DateTime.Now - _rebootStartedAt).TotalSeconds > 20)
                {
                    _rebooting = false;
                    _expectAfterReboot = null;
                    Disconnect("重启后设备未回来（超过 20 秒）");
                    Warn("设备重启后没有重新出现。\n\n请检查 USB 线，或拔插一次。\n（配置已保存，不会丢）");
                    return;
                }

                _lblDevice.Text = $"⏳ 设备正在重启…（已 {(DateTime.Now - _rebootStartedAt).TotalSeconds:F0}s）";
                _lblDevice.ForeColor = DarkTheme.Warn;
                if (_dev.IsOpen) Disconnect("设备正在重启");
                return;
            }

            if (_dev.IsOpen) Disconnect("设备已拔出");
            _lblDevice.Text = $"⚪ 未检测到设备（VID:0x{KbDevice.Vid:X4} PID:0x{KbDevice.Pid:X4}）";
            _lblDevice.ForeColor = DarkTheme.TextDim;
            SetStatus("未检测到设备 —— 界面上显示的是出厂默认值，没有写入任何设备");
            return;
        }

        if (_dev.IsOpen)
        {
            // 设备重新枚举后老句柄会失效，但 IsOpen 仍是 true —— 主动发现并恢复
            if (_dev.LooksDead(out var why))
            {
                SetStatus($"检测到设备句柄已失效（{why}），正在自动重连…");
                if (TryRecoverDevice(out var note))
                    SetStatus($"✔ 已自动重连：{note}");
                else
                    Disconnect($"自动重连失败（{note}）");
            }
            return;
        }

        // 设备出现就自动连上（用户自己点过「断开」则不擅自连回）
        if (!_userDisconnected && AutoConnect()) return;

        _lblDevice.Text = "🟡 检测到设备，点击「连接设备」";
        _lblDevice.ForeColor = DarkTheme.Warn;
    }

    private bool AutoConnect()
    {
        if (!_dev.Open()) return false;

        var (ok, detail) = _dev.Probe();
        _dev.StartReading();
        _btnConnect.Text = "断开设备";
        SetToolbarEnabled(true);
        ShowDeviceLine(ok, detail);

        if (_rebooting)
        {
            _rebooting = false;
            SetStatus("✔ 设备已重启并重新连接，正在回读配置比对…");
            VerifyAfterReboot();
            return ok;
        }

        // ★ 连上就把设备里的真实配置读回界面。
        //   否则界面是默认值、设备是另一份，用户一点保存就把设备覆盖了。
        SetStatus("已自动连接设备，正在读取设备配置…");
        DoRead();
        return ok;
    }

    private void ShowDeviceLine(bool ok, string detail)
    {
        _lblDevice.Text = $"{(ok ? "🟢" : "🟡")} {_dev.ManufacturerString} / {_dev.ProductString}    " +
                          $"序列号: {(string.IsNullOrEmpty(_dev.SerialString) ? "（无）" : _dev.SerialString)}    " +
                          $"握手: {(ok ? "✔ " + detail : "✗ " + detail)}";
        _lblDevice.ForeColor = ok ? DarkTheme.Ok : DarkTheme.Warn;
    }

    private void Connect()
    {
        _userDisconnected = false;

        if (!_dev.Open())
        {
            SetStatus("打开设备失败：找不到厂商配置集合 (UP:0xFF00)");
            MessageBox.Show(
                "无法打开厂商配置集合。\n\n" +
                "常见原因：\n" +
                "  1. 设备未插好 / 固件未运行\n" +
                "  2. 被其他程序占用\n" +
                "  3. 固件的厂商接口 Usage Page 不是 0xFF00",
                "连接失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var (ok, detail) = _dev.Probe();
        ShowDeviceLine(ok, detail);
        _dev.StartReading();
        _btnConnect.Text = "断开设备";
        SetToolbarEnabled(true);
        SetStatus(ok ? "已连接，正在接收输入状态（20Hz）" : "已连接，但握手未通过");
        DoRead();
    }

    private void Disconnect() => Disconnect("已断开");

    private void Disconnect(string reason)
    {
        _dev.Close();
        _btnConnect.Text = "连接设备";
        _lblDevice.Text = "⚪ " + reason;
        _lblDevice.ForeColor = DarkTheme.TextDim;
        _layout.SetDisconnected(reason);
        SetToolbarEnabled(false);
        SetStatus(reason);
    }

    private void SetToolbarEnabled(bool on)
    {
        _btnRead.Enabled    = on;
        _btnSave.Enabled    = on;
        _macro.AttachDevice(on);
        _layout.AttachDevice(on);
    }

    private void OnInputReceived(InputState st)
    {
        _layout.UpdateState(st);      // 「按键布局」页的实时高亮 + 读数
        _macro.UpdateLiveState(st);
    }

    private void SetStatus(string s)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(() => SetStatus(s)); } catch { } return; }
        _statusText.Text = s;
    }

    /// <summary>把失败原因明确弹出来 —— 只写状态栏用户注意不到，会以为"点了没反应"</summary>
    private void Warn(string msg)
    {
        SetStatus("✗ " + msg.Replace("\n\n", " "));
        MessageBox.Show(msg, "操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    // ══════════════════════════════════════════════════════════════
    //  句柄失效恢复
    // ══════════════════════════════════════════════════════════════

    private bool TryRecoverDevice(out string note)
    {
        note = "";
        var before = _dev.ReopenCount;

        if (!_dev.Reopen())
        {
            note = "重新打开失败（枚举不到厂商配置集合 UP:0xFF00）";
            return false;
        }

        _dev.StartReading();
        var (ok, detail) = _dev.Probe();
        note = $"第 {_dev.ReopenCount} 次重连，{(ok ? "握手 ✔ " + detail : "握手 ✗ " + detail)}";
        ShowDeviceLine(ok, detail);
        if (_dev.ReopenCount != before) SetToolbarEnabled(true);
        return ok;
    }

    private bool RetryAfterReconnect(string what)
    {
        SetStatus($"{what}失败，正在尝试自动重连后重试…");
        if (!TryRecoverDevice(out var note))
        {
            SetStatus($"✗ {what}失败，且自动重连也未成功：{note}");
            return false;
        }
        SetStatus($"已重连（{note}），重试{what}…");
        return true;
    }

    // ══════════════════════════════════════════════════════════════
    //  配置读写
    // ══════════════════════════════════════════════════════════════

    private void DoRead()
    {
        if (!Connected) { Warn("设备未连接，请先点「连接设备」。"); return; }

        var raw = _dev.ReadConfig();
        if (raw is null)
        {
            if (_dev.Ping())
            {
                Warn("读取被设备拒绝或应答超时（CFG_READ）。\n\n请点「断开」再「连接设备」后重试。");
                return;
            }
            if (!RetryAfterReconnect("读取")) { Warn("读取失败：连不上设备，请检查 USB 线并重新连接。"); return; }
            raw = _dev.ReadConfig();
            if (raw is null) { Warn("读取失败：重连后仍然读不到配置。"); return; }
        }

        var (ok, reason) = KbConfig.Validate(raw);
        if (!ok) { SetStatus($"配置校验失败：{reason}"); Warn($"读到的配置校验不通过：\n\n{reason}"); return; }

        _session.Replace(KbConfig.FromBytes(raw));

        // 基线取「规范化之后」的字节：这样刚读回来不会因为界面顺手把宏计数
        // 规范化一下，就误报成"有未保存改动"
        _lastSavedBytes = _session.Config.ToBytes();
        SetDirty(false);
        SetStatus($"已从设备读回 {raw.Length} 字节并校验通过（{reason}）");
    }

    private bool DoSave()
    {
        if (!Connected) { Warn("设备未连接，请先点「连接设备」。"); return false; }

        _session.CollectFromUi();
        var cfg = _session.Config;
        var bytes = cfg.ToBytes();

        var (ok, reason) = KbConfig.Validate(bytes);
        if (!ok) { SetStatus($"本地配置自检失败：{reason}"); Warn($"本地配置自检不通过：\n\n{reason}"); return false; }

        if (!_dev.WriteConfig(bytes))
        {
            if (_dev.Ping()) { Warn("写入被设备拒绝（CFG_WRITE NACK）。\n\n请点「从设备读取」确认当前配置，再重试。"); return false; }
            if (!RetryAfterReconnect("写入")) { Warn("保存失败：连不上设备，请检查 USB 线并重新连接。"); return false; }
            if (!_dev.WriteConfig(bytes)) { Warn("保存失败：重连后写入仍被拒绝。"); return false; }
        }

        var saveRes = _dev.SaveConfig();
        var sdetail = saveRes.Detail;

        if (!saveRes.Ok)
        {
            if (saveRes.Detail.Contains("频繁"))
            {
                Warn("设备拒绝落盘：一分钟内保存次数超过配额（防写入风暴）。\n\n" +
                     "★ 配置【已经在设备里生效】，可以正常使用；\n" +
                     "   只是还没写进 NVS，现在断电会丢。\n\n" +
                     "等一分钟再点一次「保存到设备」即可落盘。");
                return false;
            }

            if (_dev.Ping()) { Warn($"保存被设备拒绝：{saveRes.Detail}"); return false; }
            if (!RetryAfterReconnect("提交")) { Warn($"保存失败：{saveRes.Detail}"); return false; }
            if (!_dev.WriteConfig(bytes)) { Warn("保存失败：重连后重新写入被拒绝。"); return false; }

            var retrySave = _dev.SaveConfig();
            if (!retrySave.Ok) { Warn($"保存失败：重连后仍然提交不了（{retrySave.Detail}）。"); return false; }
            sdetail = "已生效并落盘（重连后重试成功）";
        }

        _lastSavedBytes = bytes;
        SetDirty(false);
        SetStatus($"✔ 保存成功：{sdetail}（宏数量 {cfg.MacroCount}）");
        return true;
    }

    private void DoDefault()
    {
        if (MessageBox.Show(
                "把界面上所有设置恢复成出厂默认值？\n\n" +
                "（只改界面，点「保存到设备」才会写进设备）",
                "恢复默认值", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;

        _session.Replace(KbConfig.CreateDefault());
        SetDirty(true);
        SetStatus("已填入出厂默认值 —— 点「保存到设备」才会写进设备");
    }

    /// <summary>
    /// 「按键布局」页的弹面板改了某个槽位。
    ///
    /// ★ 为什么不走 IConfigEditor.WriteToModel：这一页和旧「按键映射」页
    ///   编辑的是**同一批字段**（槽位 0..3 → Buttons[0..3]、槽位 10 → Buttons[4]、
    ///   槽位 16..19 → 摇杆方向键）。两个页签都做批量回写的话，
    ///   CollectFromUi 按注册顺序调用，后跑的会静默覆盖先跑的 ——
    ///   在一边改完、切到另一边再点保存，改动就没了。
    ///   所以改成"改完立刻由 MainForm 单向写入"，并**同步刷新旧页的界面**。
    /// </summary>
    private void OnSlotMappingChanged(int slot, byte code, byte mod)
    {
        if (!SlotMap.WriteSlot(_session.Config, slot, code, mod))
        {
            // 区分两种拒绝原因，别让用户以为是程序坏了
            if (!SlotMap.SlotSupported(slot))
                SetStatus($"✗ 槽位 {slot} 在当前固件里没有存储位置，改动未生效");
            else
                SetStatus($"✗ 槽位 {slot} 存不了组合键（旧固件的摇杆方向键只有键码，没有修饰键字段）"
                        + " —— 请改选单个按键，改动未生效");
            return;
        }

        // 旧「按键映射」页显示的是同一批字段，必须同步刷新；
        // 否则那边还显示旧值，用户切过去再保存就把刚才的改动覆盖回去了
        SetDirty(true);

        var what = code == 0 && mod == 0
            ? "不映射"
            : $"键码 0x{code:X2}" + (mod != 0 ? $" / 修饰键 0x{mod:X2}" : "");
        SetStatus($"✔ 槽位 {slot} 已改为 {what} —— 点「保存并重启」写入设备");
    }

    /// <summary>「按键布局」页改了某个槽位的启用开关</summary>
    private void OnSlotEnabledChanged(int slot, bool on)
    {
        if (!SlotMap.WriteSlotEnabled(_session.Config, slot, on))
        {
            SetStatus($"✗ 槽位 {slot} 不支持启用/禁用（固件里只有按键有 flags 字段）");
            return;
        }
        SetDirty(true);
        SetStatus($"✔ 槽位 {slot} 已{(on ? "启用" : "禁用")} —— 点「保存并重启」写入设备");
    }

    /// <summary>「按键布局」页改了摇杆死区 / 反向（0x0004 起两个摇杆各一套）</summary>
    private void OnStickSettingsChanged(int stickIdx, int deadzonePct, bool invertX, bool invertY)
    {
        if (stickIdx < 0 || stickIdx >= KbConfig.StickCount)
        {
            SetStatus($"✗ 摇杆下标 {stickIdx} 越界");
            return;
        }

        _session.Config.Sticks[stickIdx].DeadzonePct = (byte)Math.Clamp(deadzonePct, 0, 60);
        _session.Config.Sticks[stickIdx].InvertX = (byte)(invertX ? 1 : 0);
        _session.Config.Sticks[stickIdx].InvertY = (byte)(invertY ? 1 : 0);
        SetDirty(true);

        string[] which = { "左摇杆", "右摇杆" };
        SetStatus($"✔ {which[stickIdx]}：死区 {deadzonePct}%"
                + (invertX ? " / X 反向" : "") + (invertY ? " / Y 反向" : "")
                + " —— 点「保存并重启」写入设备");
    }

    private void DoCalibReset()    {
        if (!Connected) { SetStatus("设备未连接"); return; }

        if (MessageBox.Show(
                "重置会把摇杆量程恢复满量程并重新自学习，同时重跑中心校准。\n\n" +
                "★ 执行时请勿触碰摇杆（中心值取自当前读数），否则会校歪。\n\n确定继续？",
                "重置摇杆校准", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;

        var (ok, detail) = _dev.ResetCalibration();
        SetStatus(ok ? $"{detail}。把摇杆推到底几次即可收敛。" : detail);
    }

    // ══════════════════════════════════════════════════════════════
    //  保存并重启（一键应用新布局）
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 点一下 = 保存到设备 → 软重启 → 自动重连 → 回读比对。
    ///
    /// ★ 为什么合成一个按钮：改完映射后，配置虽然已经"生效"了，
    ///   但设备上正在跑的宏、按键状态、以及行程自学习等运行时状态都还是旧的。
    ///   重启一次从 NVS 重新加载，才是最干净的"应用新布局"。
    ///   以前要先点「保存」再点「重启设备」两步，容易只做一半就以为生效了。
    ///
    /// ★ 没有二次确认框：按钮名字已经说清楚要做两件事，再弹框只是拖慢操作。
    ///   只在【失败】时才弹框 —— 成功就静默走完，状态栏给结论。
    /// </summary>
    private void DoSaveAndReboot()
    {
        if (!Connected) { Warn("设备未连接，请先点「连接设备」。"); return; }

        // 1) 保存
        if (!DoSave()) return;                 // 失败时 DoSave 已经弹过框了
        var expect = _lastSavedBytes;

        // 2) 软重启
        var res = _dev.RebootDevice();
        if (!res.Ok) { Warn($"保存成功，但重启命令失败：{res.Detail}\n\n请手动拔插一次 USB 让配置生效。"); return; }

        _rebooting = true;
        _rebootStartedAt = DateTime.Now;
        _expectAfterReboot = expect;

        SetToolbarEnabled(false);
        _lblDevice.Text = "⏳ 正在重启以应用新布局…";
        _lblDevice.ForeColor = DarkTheme.Warn;
        SetStatus($"已保存；{res.Detail}，正在等待设备回来…");

        if (!res.Flushed)
            Warn("设备回复：重启前的落盘失败了。\n\n本次改动可能没写进 NVS，" +
                 "重启后会回到上一次成功保存的配置。");
    }

    private void VerifyAfterReboot()
    {
        var expect = _expectAfterReboot;
        _expectAfterReboot = null;

        var raw = _dev.ReadConfig();
        if (raw is null) { SetStatus("设备已重启并重连，但回读配置失败。"); return; }

        SetToolbarEnabled(true);
        _session.Replace(KbConfig.FromBytes(raw));
        SetDirty(false);

        if (expect is null) { SetStatus("✔ 设备已重启并自动重连，配置已回读。"); return; }

        if (raw.SequenceEqual(expect))
        {
            // ★ 成功不弹框：这是"保存并重启"的常态路径，每次都弹会烦。
            //   结论写在状态栏和硬件信息行上；只有【失败】才弹框。
            SetStatus($"✔ 已完成保存并重启，新布局已生效（回读 {raw.Length} 字节，与保存前逐字节一致）");
            _lblDevice.Text += "    已应用新布局 ✔";
        }
        else
        {
            int at = -1;
            for (int i = 0; i < Math.Min(raw.Length, expect.Length); i++)
                if (raw[i] != expect[i]) { at = i; break; }

            SetStatus($"⚠ 重启后回读与保存前不一致（首个差异在偏移 {at}）");
            MessageBox.Show(
                "设备已重启并自动重连，但回读的配置与保存前【不一致】。\n\n" +
                $"首个差异在偏移 {at}：保存前 0x{expect[at]:X2} / 重启后 0x{raw[at]:X2}\n\n" +
                "可能原因：\n  · 保存时撞上 NVS 写入配额（一分钟最多 10 次），没能落盘\n\n" +
                "界面现在显示的是设备【当前实际】的配置 —— 再点一次「保存并重启」即可。",
                "保存未生效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  未保存改动
    // ══════════════════════════════════════════════════════════════

    // 「设备接口列表」那个诊断窗口已按需求删除（原来挂在最上方的「设备▾」下拉里）。
    // 需要时用命令行看即可：KbConfigurator.exe --selftest 会打印完整枚举结果。
}
