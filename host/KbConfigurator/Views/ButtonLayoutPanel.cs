using System.Diagnostics;
using KbConfigurator.Model;
using KbConfigurator.Protocol;
using KbConfigurator.Ui;

namespace KbConfigurator.Views;

/// <summary>
/// 「按键布局」页 —— 手柄示意图 + 热区层 + 底部实时读数条。
///
/// 结构（五步都已完成）：
///   图片 + layout.json 坐标 + 热区层 / 实时高亮 + 底部读数条 /
///   点热区弹面板改映射 / 坐标校准模式 / 已合并旧「输入调试」「按键映射」两页
///
/// ★ 布局：图**占满整页**（等比缩放居中），数值读数收到底部一条。
///   图本来就密集，不给它让位就看不清；读数原来占一整块，改成一条足够。
///
/// ★ 读数条里的标签全部 AutoSize=false + Dock=Fill + AutoEllipsis：
///   用 AutoSize 的话窗口缩到最小尺寸时标签会把容器撑宽，
///   布局验收里的"不越界"断言就会红（旧「输入调试」页踩过这个坑，
///   5 个指示灯需要 490px 而最小窗口只有 484px，第二行被裁掉了）。
/// </summary>
public sealed class ButtonLayoutPanel : UserControl, IConfigEditor
{
    private readonly DiagramView _diagram = new();
    private readonly TableLayoutPanel _root;

    // ── 底部读数条 ──
    private readonly Label _lblLStick  = Rd("左摇杆  —");
    private readonly Label _lblRStick  = Rd("右摇杆  —");
    private readonly Label _lblDirs    = Rd("方向  —");
    private readonly Label _lblFps     = Rd("— 帧/秒", DarkTheme.TextDim);

    /// <summary>原始 GPIO 电平 + 运行时间（诊断接线用，原在旧「输入调试」页）</summary>
    private readonly Label _lblRaw     = Rd("raw —", DarkTheme.TextDim);
    private readonly Button _btnCalib  = L.Button("重置摇杆校准", 116);

    private readonly Label _lblTravel  = Rd("行程  L[—,—]  R[—,—]", DarkTheme.TextDim);
    private readonly Label _lblButtons = Rd("按键  —");
    private readonly Label _lblMacro   = Rd("宏  空闲", DarkTheme.TextDim);
    private readonly Label _lblMode    = Rd("映射  —", DarkTheme.TextDim);

    private readonly Label _lblLayout  = Rd("", DarkTheme.TextDim);

    // ── 布局校准 ──
    private readonly Button _btnCalibMode  = L.Button("校准布局", 96);
    private readonly Button _btnCalibSave  = L.Button("保存坐标", 88, DarkTheme.Accent);
    private readonly Button _btnCalibReset = L.Button("恢复出厂坐标", 104);

    /// <summary>读数条第 3 行（状态文字 + 校准按钮）—— 要改列跨度</summary>
    private TableLayoutPanel? _statusRow;

    /// <summary>进入校准时的坐标快照，「退出」时还原（= 取消未保存的拖动）</summary>
    private List<XboxLayout.CoordSnapshot>? _calibSnapshot;
    private bool _calibDirty;

    // ── 状态 ──
    private XboxLayout? _layout;
    private string _loadNote = "";
    private readonly SlotMap.Highlights _hl = new();

    private readonly Stopwatch _fpsWatch = Stopwatch.StartNew();
    private int _frames;
    private double _fps;
    private uint _lastSeq;
    private long _dropped;

    private readonly ToolTip _tip = new() { InitialDelay = 350, ReshowDelay = 150 };
    private LayoutItem? _tipItem;

    /// <summary>上一次是断开状态（用来在收到第一帧时清掉读数条上的说明文字）</summary>
    private bool _wasDisconnected = true;

    /// <summary>请求重置摇杆校准（由 MainForm 接过去，因为它要操作设备）</summary>
    public event Action? RequestCalibration;

    /// <summary>用户在弹面板里改了某个槽位的映射（slot, 键码, 修饰键）—— 由 MainForm 写进配置</summary>
    public event Action<int, byte, byte>? SlotMappingChanged;

    /// <summary>用户在弹面板里改了某个槽位的启用开关</summary>
    public event Action<int, bool>? SlotEnabledChanged;

    /// <summary>用户在弹面板里改了摇杆死区 / 反向（摇杆下标 0=左 1=右, 死区, X反向, Y反向）</summary>
    public event Action<int, int, bool, bool>? StickSettingsChanged;

    /// <summary>当前配置（由 <see cref="LoadFromModel"/> 传入，只读用来显示）</summary>
    private KbConfig? _cfg;

    /// <summary>界面上 24 个槽位的映射（WriteToModel 写回用）</summary>
    private (byte Code, byte Mod)[]? _slotVals;
    private bool[]? _slotOn;

    /// <summary>两个摇杆各自的轴向参数（0x0004 起每摇杆独立）</summary>
    private readonly int[]  _stickDeadzone = { 12, 12 };
    private readonly bool[] _stickInvX = new bool[KbConfig.StickCount];
    private readonly bool[] _stickInvY = new bool[KbConfig.StickCount];

    public ButtonLayoutPanel()
    {
        Dock = DockStyle.Fill;
        BackColor = DarkTheme.FormBack;

        _root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = DarkTheme.FormBack,
            Padding = new Padding(8, 6, 8, 6),
        };
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));    // 图占满剩余高度
        // ★ 读数条高度必须**算够**：3 行 × 26px + 上下 padding 8px = 86。
        //   第一版给的是 76，于是每行只有 21px，而一个 8.5pt 标签的自然高度是
        //   23px，直接越界 —— --layout 的"控件不越界"断言抓到了。
        //   行高用 Absolute 而不是 Percent：Percent 算出来是小数，
        //   四舍五入后可能刚好比内容少 1px，这种问题只在特定窗口尺寸暴露。
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, ReadoutBarHeight));

        _diagram.Dock = DockStyle.Fill;
        _diagram.Margin = new Padding(0);
        _root.Controls.Add(_diagram, 0, 0);
        _root.Controls.Add(BuildReadoutBar(), 0, 1);

        Controls.Add(_root);

        DarkTheme.SetTransparent(_diagram);
        _diagram.MouseMove += (_, _) => UpdateHoverText();
        _diagram.HotspotActivated += OnHotspotActivated;
        _btnCalib.Click += (_, _) => RequestCalibration?.Invoke();

        // ── 布局校准（设计文档 §四）──
        _btnCalibMode.Click  += (_, _) => SetCalibrationMode(!_diagram.CalibrationMode);
        _btnCalibSave.Click  += (_, _) => SaveCalibration();
        _btnCalibReset.Click += (_, _) => ResetCalibration();
        _diagram.LayoutEdited += () => { _calibDirty = true; UpdateCalibUi(); };

        LoadLayout();
        SetDisconnected("设备未连接");
    }

    public XboxLayout? LayoutData => _layout;
    public DiagramView Diagram => _diagram;
    public string LoadNote => _loadNote;
    public SlotMap.Highlights Highlights => _hl;

    // ══════════════════════════════════════════════════════════════
    //  读数条
    // ══════════════════════════════════════════════════════════════

    /// <summary>读数条里的固定尺寸标签（见类注释：不能用 AutoSize）</summary>
    private static Label Rd(string text, Color? c = null) => new()
    {
        Text = text,
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true,
        ForeColor = c ?? DarkTheme.Text,
        Font = DarkTheme.Mono(8.5f),
    };

    /// <summary>读数条里每一行的高度（Absolute，防止小数取整后差 1px）</summary>
    /// <summary>
    /// 读数条每行的高度。
    /// ★ 必须容得下这一行里的按钮：L.Button 造出来的按钮 Height=28，
    ///   行高只有 26 时按钮会溢出 2px 被父容器裁掉 ——
    ///   表现是按钮**文字的上半截被切**（用户报的"校准布局只显示了一半"）。
    ///   ★ 光断言"客户区高度 ≥ 文字高度"抓不到这个：客户区是布局算出来的，
    ///     28 也是"够的"，真正出问题的是按钮比行高还高、被裁在绘制阶段。
    ///     所以这里给足 32（28 + 上下各 2px 边距）。
    /// </summary>
    private const int ReadoutRowHeight = 32;

    /// <summary>frame 的上外边距</summary>
    private const int ReadoutTopMargin = 4;

    /// <summary>frame 的上下内边距合计（对应 Padding 的 4+4）</summary>
    private const int ReadoutVPadding = 8;

    /// <summary>
    /// 读数条总高 = 3 行 + 上外边距 + 上下内边距。
    ///
    /// ★ 这三个数必须**一起算**：第一版只写了"3 行 + padding"，漏了 frame 自己的
    ///   4px 外边距，于是内层 3 行共 78px 挤在 74px 里，越界 4px。
    ///   这种差几像素的问题不写断言根本看不见，是 --layout 抓出来的。
    /// </summary>
    private const int ReadoutBarHeight =
        ReadoutRowHeight * 3 + ReadoutTopMargin + ReadoutVPadding;

    private Control BuildReadoutBar()
    {
        var frame = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = DarkTheme.PanelBack,
            Padding = new Padding(8, ReadoutVPadding / 2, 8, ReadoutVPadding / 2),
            Margin = new Padding(0, ReadoutTopMargin, 0, 0),
        };

        var rows = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = DarkTheme.PanelBack,
            AutoSize = false,
        };
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        for (int i = 0; i < 3; i++)
            rows.RowStyles.Add(new RowStyle(SizeType.Absolute, ReadoutRowHeight));

        // ── 第 1 行：两摇杆读数 + 方向 + 帧率 + 原始电平/运行时间 + 校准按钮 ──
        // ★ 最后那个百分比列是"原始 GPIO 电平 + 运行时间"，
        //   原来是旧「输入调试」页的诊断信息，合并时搬过来 —— 删页不能丢功能。
        //   raw 一闪一闪能看出某根线是不是接错了；运行时间归零说明设备偷偷重启过。
        var r0 = L.Grid(-26, -26, -8, -9, -16, 124);
        r0.AutoSize = false;
        L.RowHeight(r0, 0, ReadoutRowHeight);   // ★ 不设的话行按内容自适应，会顶破外框
        Add(r0, _lblLStick, 0, 0);
        Add(r0, _lblRStick, 1, 0);
        Add(r0, _lblDirs, 2, 0);
        Add(r0, _lblFps, 3, 0);
        Add(r0, _lblRaw, 4, 0);
        _btnCalib.Margin = new Padding(0, 2, 0, 2);
        _btnCalib.Dock = DockStyle.Fill;
        r0.Controls.Add(_btnCalib, 5, 0);

        // ── 第 2 行：行程 + 按键 + 宏 + 映射模式 + 校准入口 ──
        // ★ 列宽是**负权重按和取百分比**，不是"哪个负得少哪个占满"。
        //   第一版写成 (-22,-22,-10,-1)，最后那列只分到 1/55 ≈ 1.8%，
        //   于是"映射"文字被截成一个"映"字 —— 截图才看出来。
        var r1 = L.Grid(-22, -22, -10, -36, 96);
        r1.AutoSize = false;
        L.RowHeight(r1, 0, ReadoutRowHeight);
        Add(r1, _lblTravel, 0, 0);
        Add(r1, _lblButtons, 1, 0);
        Add(r1, _lblMacro, 2, 0);
        Add(r1, _lblMode, 3, 0);
        _btnCalibMode.Margin = new Padding(0, 2, 0, 2);
        _btnCalibMode.Dock = DockStyle.Fill;
        r1.Controls.Add(_btnCalibMode, 4, 0);

        // ── 第 3 行：布局自检状态；进校准模式后让位给 保存/恢复默认 ──
        //   「退出校准」不在这里 —— 第 2 行那个切换按钮已经管进出了，
        //   再放一个同名的只会让人不知道该点哪个。
        var r2 = L.Grid(-1, 96, 112);
        r2.AutoSize = false;
        L.RowHeight(r2, 0, ReadoutRowHeight);
        _statusRow = r2;
        Add(r2, _lblLayout, 0, 0);
        r2.SetColumnSpan(_lblLayout, 3);      // 平时状态文字占满整行
        foreach (var (b, col) in new[] { (_btnCalibSave, 1), (_btnCalibReset, 2) })
        {
            b.Margin = new Padding(0, 2, 6, 2);
            b.Dock = DockStyle.Fill;
            b.Visible = false;
            r2.Controls.Add(b, col, 0);
        }

        rows.Controls.Add(r0, 0, 0);
        rows.Controls.Add(r1, 0, 1);
        rows.Controls.Add(r2, 0, 2);

        frame.Controls.Add(rows);
        return frame;
    }

    private static void Add(TableLayoutPanel t, Control c, int col, int row)
    {
        c.Margin = new Padding(0, 1, 8, 1);
        t.Controls.Add(c, col, row);
    }

    // ══════════════════════════════════════════════════════════════
    //  布局加载
    // ══════════════════════════════════════════════════════════════

    private void LoadLayout()
    {
        var path = XboxLayout.DefaultPath;
        if (!File.Exists(path))
        {
            _loadNote = $"找不到布局文件：{path}";
            SetLayoutStatus("✗ " + _loadNote, DarkTheme.Error);
            return;
        }

        try
        {
            _layout = XboxLayout.Load(path);
        }
        catch (Exception ex)
        {
            _loadNote = $"布局文件解析失败：{ex.Message}";
            SetLayoutStatus("✗ " + _loadNote, DarkTheme.Error);
            return;
        }

        var (ok, reason) = _layout.Validate();
        _loadNote = reason;

        if (!ok)
        {
            SetLayoutStatus("✗ 布局自检不通过：" + reason, DarkTheme.Error);
            return;
        }

        SetLayoutStatus($"✔ 布局 {reason}", DarkTheme.Ok);

        // ★ 真的去加载图片（不做测试旁路）：资源没被复制到输出目录是这套代码
        //   最容易踩的坑，而它只在运行期暴露。让布局验收走真实路径才能抓到。
        _diagram.Load(_layout);
        if (_diagram.LoadError.Length > 0)
            SetLayoutStatus("⚠ " + _loadNote + "  ／ " + _diagram.LoadError, DarkTheme.Warn);

        // 0x0004 起没有"映射模式"这回事了 —— 固件的槽位定义和界面对齐，
        // 位号即槽位号。这里改成显示规模，让用户一眼知道在跟什么打交道。
        _lblMode.Text = $"配置 0x{KbConfig.Version:X4} · {KbConfig.SlotCount} 槽位 · "
                      + $"{KbConfig.StickCount} 摇杆 · {KbConfig.Size} 字节";
        _lblMode.ForeColor = DarkTheme.Ok;
    }

    private void SetLayoutStatus(string s, Color c)
    {
        _lblLayout.Text = s;
        _lblLayout.ForeColor = c;
    }

    // ══════════════════════════════════════════════════════════════
    //  实时状态
    // ══════════════════════════════════════════════════════════════

    /// <summary>每来一帧 Input Report 调一次（20Hz）</summary>
    public void UpdateState(InputState st)
    {
        // ★ 断开时 SetDisconnected 会把说明文字写进帧率列，而帧率要攒够 1 秒才更新 ——
        //   于是刚连上的那一秒里，帧率列显示的还是「设备未连接」，但左摇杆已经在跳数了。
        //   自相矛盾而且很容易让人以为设备有问题。第一帧就把它清掉。
        if (_wasDisconnected)
        {
            _wasDisconnected = false;
            _frames = 0;
            _dropped = 0;
            _lastSeq = 0;
            _fpsWatch.Restart();
            _lblFps.Text = "采样中…";
            _lblFps.ForeColor = DarkTheme.TextDim;
        }

        SlotMap.Fill(st, _hl);
        _diagram.SetHighlights(_hl);

        // ── 帧率与丢帧 ──
        _frames++;
        if (st.Seq != 0 && _lastSeq != 0 && st.Seq > _lastSeq + 1)
            _dropped += st.Seq - _lastSeq - 1;
        _lastSeq = st.Seq;

        if (_fpsWatch.ElapsedMilliseconds >= 1000)
        {
            _fps = _frames * 1000.0 / _fpsWatch.ElapsedMilliseconds;
            _frames = 0;
            _fpsWatch.Restart();
            _lblFps.Text = $"{_fps:F1} 帧/秒" + (_dropped > 0 ? $"  丢 {_dropped}" : "");
            _lblFps.ForeColor = _dropped > 0 ? DarkTheme.Warn : DarkTheme.TextDim;
        }

        // ── 两个摇杆（0x0004 起右摇杆也有了）──
        // ★ 两轴都故障时把整行压成一句话。
        //   逐轴写 "X —（未接线/已禁用） Y —（未接线/已禁用）" 有 30 多个字，
        //   比列宽还长 —— 会被截断，而且把右边的「方向」列一起挤没（截图里看到了）。
        string AxisText(string label, int ax, int ay)
        {
            if (st.FaultOf(ax) && st.FaultOf(ay)) return $"{label} 未接线（两轴已禁用）";
            return $"{label} X{FormatAxis(st, ax)}  Y{FormatAxis(st, ay)}";
        }

        _lblLStick.Text = AxisText("左摇杆", InputState.AxisLX, InputState.AxisLY);
        _lblLStick.ForeColor = st.FaultOf(InputState.AxisLX) || st.FaultOf(InputState.AxisLY)
            ? DarkTheme.Error : DarkTheme.Text;

        _lblRStick.Text = AxisText("右摇杆", InputState.AxisRX, InputState.AxisRY);
        _lblRStick.ForeColor = st.FaultOf(InputState.AxisRX) || st.FaultOf(InputState.AxisRY)
            ? DarkTheme.Error : DarkTheme.Text;

        // ── 方向 / 按键 ──
        // ★ 三组方向分开显示：十字键 / 左摇杆 / 右摇杆。
        //   0x0003 只有一组，三个来源会互相串。
        _lblDirs.Text = $"十 {InputState.DirText(st.DirsDpad)}"
                      + $"  左 {InputState.DirText(st.DirsLstick)}"
                      + $"  右 {InputState.DirText(st.DirsRstick)}";
        _lblDirs.ForeColor = (st.DirsDpad | st.DirsLstick | st.DirsRstick) != 0
            ? DarkTheme.Ok : DarkTheme.TextDim;

        _lblButtons.Text = "按键  " + ActiveButtonText();

        // ── 宏 ──
        _lblMacro.Text = st.MacroBusy ? "宏  执行中" : "宏  空闲";
        _lblMacro.ForeColor = st.MacroBusy ? DarkTheme.Ok : DarkTheme.TextDim;

        // ── 行程（四轴）──
        bool learned = st.TravelLearnedOf(InputState.AxisLX) && st.TravelLearnedOf(InputState.AxisLY)
                    && st.TravelLearnedOf(InputState.AxisRX) && st.TravelLearnedOf(InputState.AxisRY);
        _lblTravel.Text = $"行程 L[{st.TMin[0]},{st.TMax[0]}]/[{st.TMin[1]},{st.TMax[1]}]"
                        + $"  R[{st.TMin[2]},{st.TMax[2]}]/[{st.TMin[3]},{st.TMax[3]}]"
                        + (learned ? " ✔收敛" : " 未收敛");
        _lblTravel.ForeColor = learned ? DarkTheme.TextDim : DarkTheme.Warn;

        // ── 原始电平 + 运行时间（接线诊断）──
        // ★ 3 字节 = 24 个槽位；未接线的脚被上拉读高，所以 bit 恒 0，不会乱闪
        _lblRaw.Text = $"raw {st.BtnRaw[3]:X2}{st.BtnRaw[2]:X2}{st.BtnRaw[1]:X2}{st.BtnRaw[0]:X2}"
                     + $"  {st.UptimeMs / 1000.0:F1}s";

        if (st.FaultText().Length > 0)
            SetLayoutStatus("⚠ " + st.FaultText(), DarkTheme.Error);
    }


    /// <summary>
    /// 单个轴的读数文字。
    ///
    /// ★ 故障轴要显示成文字而不是数字：没接线的轴会读到贴轨值（实测 20 左右、-98%），
    ///   显示成 "21(-98%)" 看着像真实读数，很容易让人以为摇杆接反了或者坏了。
    ///   实际是"这根轴压根没接线，已被固件禁用、不参与方向判定"。
    ///   正在接线的时候这个区别很重要 —— 否则会去调一个根本没接的轴。
    /// </summary>
    internal static string FormatAxis(InputState st, int axis)
    {
        if (axis < 0 || axis >= InputState.AxisCount) return "—";
        // ★ 这里要短：整行还有另一个轴 + 标签，太长会把相邻列挤掉
        if (st.FaultOf(axis)) return "—(未接线)";
        return $"{st.Raw[axis],4}({st.Pct[axis],4}%)";
    }

    /// <summary>一个摇杆两轴的读数文字</summary>
    internal static string AxisTextFor(string label, InputState st, int ax, int ay)
        => $"{label} X{FormatAxis(st, ax)}  Y{FormatAxis(st, ay)}";

    /// <summary>
    /// 读数条「按键」列：把当前按下的槽位名列出来。
    ///
    /// ★ 必须**包含方向槽位**（十字键 / 两个摇杆）。
    ///   原来这里把方向类整个跳过（`if (it.IsDir4) continue;`）——
    ///   而正在接线时最常焊的就是十字键，按下去读数条没反应，
    ///   很容易误判成"没焊好"。（图上高亮能看出来，但读数条是更快的核对方式。）
    /// </summary>
    internal static string ActiveSlotText(SlotMap.Highlights hl)
    {
        var names = new List<string>();
        for (int s = 0; s < SlotMap.SlotCount; s++)
            if (hl.IsSlotActive(s)) names.Add(SlotMap.NameOf(s));
        return names.Count == 0 ? "—" : string.Join(" + ", names);
    }

    private string ActiveButtonText() => ActiveSlotText(_hl);

    /// <summary>
    /// 鼠标悬停时用 ToolTip 显示光标下的项。
    ///
    /// ★ 不要把它写进读数条的某个标签：那个标签同时被实时读数占用，
    ///   两者会互相覆盖（第一版就把"方向"读数冲掉了）。
    ///   而且悬停时图上**本来就已经画出名字**了，标签是多余的 ——
    ///   ToolTip 还能顺带给出槽位号和图上原文。
    /// </summary>
    private void UpdateHoverText()
    {
        if (!IsHandleCreated) return;

        var p = _diagram.PointToClient(Cursor.Position);
        var it = _diagram.HitTest(p);

        if (ReferenceEquals(it, _tipItem)) return;      // 没换目标就别重弹
        _tipItem = it;

        if (it is null) { _tip.Hide(_diagram); return; }

        var slotText = it.Slots.Count == 0
            ? "未接线（暂缓）"
            : "槽位 [" + string.Join(",", it.Slots) + "]";
        var label = it.DiagramLabel.Length > 0 ? $"\n图上原文：{it.DiagramLabel}" : "";
        _tip.Show($"{it.Name}\n{slotText}{label}", _diagram, p.X + 16, p.Y + 20, 5000);
    }

    /// <summary>设备断开：清掉所有高亮和读数，别留着上一次的假数据</summary>
    public void SetDisconnected(string why)
    {
        _hl.Clear();
        _diagram.SetHighlights(null);
        _lblLStick.Text = "左摇杆  —";
        _lblRStick.Text = "右摇杆  —";
        _lblDirs.Text = "方向  —";
        _lblButtons.Text = "按键  —";
        _lblTravel.Text = "行程  L[—,—]  R[—,—]";
        _lblMacro.Text = "宏  空闲";
        _lblRaw.Text = "raw —";
        _lblFps.Text = why;
        _lblFps.ForeColor = DarkTheme.TextDim;
        _lastSeq = 0;
        _fps = 0;
        _frames = 0;
        _dropped = 0;
        _tipItem = null;
        _wasDisconnected = true;
        _fpsWatch.Restart();
    }

    // ══════════════════════════════════════════════════════════════
    //  布局校准（设计文档 §四）
    // ══════════════════════════════════════════════════════════════

    /// <summary>是否处于校准模式（测试/截图用）</summary>
    /// <summary>
    /// 测试用：读数条每行的高度。
    /// 用来断言"按钮不能比所在行还高" —— 高了就会被裁，表现是文字只显示半截。
    /// </summary>
    internal static int ReadoutRowHeightForTest => ReadoutRowHeight;

    public bool InCalibration => _diagram.CalibrationMode;
    public bool CalibrationDirty => _calibDirty;

    /// <summary>
    /// 进出校准模式。
    ///
    /// ★ 进入时拍一份坐标快照，「退出」时还原 = 取消未保存的拖动。
    ///   这样语义才清楚：**只有「保存坐标」会真的写文件**，
    ///   否则拖完随手一退，内存里的坐标变了、文件没变，下次启动又跳回去，
    ///   用户会以为"校准没生效"。
    /// </summary>
    public void SetCalibrationMode(bool on)
    {
        if (_layout is null) return;
        if (_diagram.CalibrationMode == on) return;

        if (on)
        {
            _calibSnapshot = _layout.Snapshot();
            _calibDirty = false;
            _diagram.CalibrationMode = true;
            SetLayoutStatus("校准模式：拖蓝色方块移动热区，拖绿色虚线框移动映射牌位置；" +
                            "改完点「保存坐标」", DarkTheme.Warn);
        }
        else
        {
            _diagram.CalibrationMode = false;
            if (_calibDirty && _calibSnapshot is not null)
            {
                XboxLayout.Restore(_calibSnapshot);
                SetLayoutStatus("已退出校准，未保存的拖动已还原", DarkTheme.TextDim);
            }
            else
            {
                SetLayoutStatus($"✔ 布局 {_loadNote}", DarkTheme.Ok);
            }
            _calibSnapshot = null;
            _calibDirty = false;
        }

        UpdateCalibUi();
        _diagram.Invalidate();
    }

    private void UpdateCalibUi()
    {
        bool on = _diagram.CalibrationMode;
        _btnCalibMode.Text = on ? "退出校准" : "校准布局";
        _btnCalibSave.Visible = on;
        _btnCalibReset.Visible = on;
        _btnCalibSave.Enabled = on && _calibDirty;
        _statusRow?.SetColumnSpan(_lblLayout, on ? 1 : 3);

        if (on && _calibDirty)
            SetLayoutStatus("有未保存的坐标改动 —— 点「保存坐标」写入 layout.json", DarkTheme.Warn);
    }

    private void SaveCalibration()
    {
        if (_layout is null) return;

        var (ok, detail) = SaveLayoutFile(_layout);
        if (!ok) { SetLayoutStatus("✗ 保存失败：" + detail, DarkTheme.Error); return; }

        // 保存成功后重新拍快照：之后「退出」就不会把刚保存的坐标又还原掉
        _calibSnapshot = _layout.Snapshot();
        _calibDirty = false;
        SetLayoutStatus("✔ 坐标已保存到 " + detail, DarkTheme.Ok);
        UpdateCalibUi();
    }

    private void ResetCalibration()
    {
        if (_layout is null) return;

        if (!_layout.ResetToFactoryDefaults(out var reason))
        {
            SetLayoutStatus("✗ " + reason, DarkTheme.Error);
            return;
        }

        _calibDirty = true;
        _diagram.Invalidate();
        SetLayoutStatus($"已{reason}（还没写文件，点「保存坐标」生效）", DarkTheme.Warn);
        UpdateCalibUi();
    }

    /// <summary>
    /// 把坐标写回文件。
    ///
    /// ★ 要写**两处**：输出目录那份是运行时真正读的；
    ///   但如果还能找到源码目录，也一起写一份 —— 否则用户校准完，
    ///   仓库里那份还是旧坐标，下次重新 clone / 别人拉下来看到的又是别的。
    ///   找不到源码目录（比如发布版）就只写输出目录，不算错误。
    /// </summary>
    private static (bool Ok, string Detail) SaveLayoutFile(XboxLayout lay)
    {
        try
        {
            lay.Save(XboxLayout.DefaultPath);

            var repo = FindRepoLayoutPath();
            if (repo is not null
                && !string.Equals(Path.GetFullPath(repo), Path.GetFullPath(XboxLayout.DefaultPath),
                                  StringComparison.OrdinalIgnoreCase))
            {
                lay.Save(repo);
                return (true, $"{XboxLayout.DefaultPath}（并同步到 {repo}）");
            }

            return (true, XboxLayout.DefaultPath);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>从输出目录往上找源码里的 Assets/layout.json（找不到返回 null）</summary>
    internal static string? FindRepoLayoutPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            // 情况一：已经在 host/KbConfigurator/ 里
            if (File.Exists(Path.Combine(dir.FullName, "KbConfigurator.csproj")))
                return Path.Combine(dir.FullName, "Assets", "layout.json");

            // 情况二：还在仓库根
            var sub = Path.Combine(dir.FullName, "host", "KbConfigurator", "KbConfigurator.csproj");
            if (File.Exists(sub))
                return Path.Combine(dir.FullName, "host", "KbConfigurator", "Assets", "layout.json");
        }
        return null;
    }

    /// <summary>与其它面板保持同一接口（MainForm 在连接/断开时统一调用）</summary>
    public void AttachDevice(bool on)
    {
        _btnCalib.Enabled = on;
        if (!on) SetDisconnected("设备未连接");
    }

    // ══════════════════════════════════════════════════════════════
    //  IConfigEditor
    // ══════════════════════════════════════════════════════════════

    public void LoadFromModel(KbConfig cfg)
    {
        _cfg = cfg;

        int n = SlotMap.SlotCount;
        _slotVals = new (byte Code, byte Mod)[n];
        _slotOn = new bool[n];
        for (int s = 0; s < n; s++)
        {
            _slotVals[s] = SlotMap.ReadSlot(cfg, s);
            _slotOn[s] = SlotMap.ReadSlotEnabled(cfg, s);
        }
        for (int k = 0; k < KbConfig.StickCount; k++)
        {
            _stickDeadzone[k] = cfg.Sticks[k].DeadzonePct;
            _stickInvX[k] = cfg.Sticks[k].InvertX != 0;
            _stickInvY[k] = cfg.Sticks[k].InvertY != 0;
        }

        RefreshSlotText();
    }

    /// <summary>
    /// 把界面上 24 个槽位的映射写回模型。
    ///
    /// ★ 这里保留的是**原始 (键码, 修饰键) 对**，不是从下拉框里读回来的 ——
    ///   下拉框表示不了的组合（例如 Ctrl+Space）如果从下拉读，会被退回"不映射"，
    ///   用户一点保存就把原值抹掉。旧「按键映射」页踩过这个坑，
    ///   现在的做法是：只在用户**真的在弹面板里改了**的时候才更新这一份数组。
    /// </summary>
    public void WriteToModel(KbConfig cfg)
    {
        for (int s = 0; s < SlotMap.SlotCount && s < (_slotVals?.Length ?? 0); s++)
        {
            var (code, mod) = _slotVals![s];
            SlotMap.WriteSlot(cfg, s, code, mod);
            if ((_slotOn?.Length ?? 0) > s) SlotMap.WriteSlotEnabled(cfg, s, _slotOn![s]);
        }

        for (int k = 0; k < KbConfig.StickCount; k++)
        {
            cfg.Sticks[k].DeadzonePct = (byte)Math.Clamp(_stickDeadzone[k], 0, 60);
            cfg.Sticks[k].InvertX = (byte)(_stickInvX[k] ? 1 : 0);
            cfg.Sticks[k].InvertY = (byte)(_stickInvY[k] ? 1 : 0);
        }

        // trigger 字段的固化不在这里 —— 已挪到 ConfigSession.Normalize()，
        // 免得"哪个页签在才做规范化"。
    }

    /// <summary>把 24 个槽位当前的映射文字 + 可编辑性算出来交给绘制层</summary>
    private void RefreshSlotText()
    {
        if (_layout is null || _cfg is null) return;

        int n = _layout.Slots.Count;
        var text = new string[n];
        var editable = new bool[n];

        for (int s = 0; s < n; s++)
        {
            // 0x0004 起 24 个槽位全都是普通按键项，**不存在"待升级"了**
            editable[s] = SlotMap.SlotSupported(s);
            var (code, mod) = SlotMap.ReadSlot(_cfg, s);
            text[s] = ShortKeyText(code, mod);
        }

        _diagram.SetSlotText(text, editable);
        // 槽位文字里已经带"不映射"，这里不再重复报"待升级"（0x0004 下不存在这个状态）
    }

    /// <summary>
    /// 键码 → 简短显示名（映射牌上位置窄，长名字放不下）。
    /// 认不出来的键码**照样显示出来**，不要显示成"不映射" —— 那是误导。
    /// </summary>
    internal static string ShortKeyText(byte code, byte mod)
    {
        if (code == 0 && mod == 0) return "不映射";

        var exact = KeyCodes.Find(code, mod);
        if (exact.Code == code && exact.Modifier == mod) return Shorten(exact.Name);

        var byCode = KeyCodes.All.FirstOrDefault(k => k.Code == code && k.Modifier == 0)
                  ?? KeyCodes.All.FirstOrDefault(k => k.Code == code);
        if (byCode is not null) return Shorten(byCode.Name);

        return $"0x{code:X2}";
    }

    /// <summary>
    /// 把长键名压短。完整名是 "↑ 上 (Up)" / "PageDown" / "Ctrl+A" 这种，
    /// 直接画到映射牌上会太长被挤成 6pt 小字。
    /// 规则：够短就原样；否则取第一个空格前的部分；再不行就截断加省略号。
    /// </summary>
    private static string Shorten(string name)
    {
        if (name.Length <= 6) return name;

        int sp = name.IndexOf(' ');
        if (sp > 0 && sp <= 6) return name[..sp];      // "↑ 上 (Up)" -> "↑"

        return name.Length <= 8 ? name : name[..8] + "…";
    }

    // ══════════════════════════════════════════════════════════════
    //  点击热区 → 弹映射编辑面板
    // ══════════════════════════════════════════════════════════════

    private void OnHotspotActivated(LayoutItem item, Point clientPt)
    {
        if (_cfg is null) return;

        var ctx = new PopupContext { Item = item };
        foreach (var s in item.Slots)
        {
            ctx.Current.Add(SlotMap.ReadSlot(_cfg, s));
            ctx.Editable.Add(SlotMap.SlotSupported(s));
            ctx.Enabled.Add(SlotMap.ReadSlotEnabled(_cfg, s));
            ctx.Notes.Add(SlotMap.MacroNoteOf(_cfg, s));
        }

        // 摇杆项额外带死区 / X、Y 反向。
        // ★ 0x0004 起**两个摇杆各有一套** —— 所以 lstick 和 rstick 都能改，
        //   值从各自的 cfg.Sticks[k] 取（0x0003 时只有左摇杆有地方存）。
        int stickIdx = SlotMap.StickIndexOfItem(item.Id);
        bool stickSettings = stickIdx >= 0;
        ctx.ShowStickSettings = stickSettings;
        if (stickSettings)
        {
            ctx.DeadzonePct = _cfg.Sticks[stickIdx].DeadzonePct;
            ctx.InvertX = _cfg.Sticks[stickIdx].InvertX != 0;
            ctx.InvertY = _cfg.Sticks[stickIdx].InvertY != 0;
        }

        // 热区在屏幕上的矩形（弹面板要贴着它出现）
        var screenPt = _diagram.PointToScreen(clientPt);
        var rect = new Rectangle(screenPt.X - 8, screenPt.Y - 8, 16, 16);

        var res = SlotMapPopup.Edit(FindForm(), rect, ctx);
        if (res is null) return;

        // 只把**真的变了**的推出去，避免无谓地把 dirty 标起来
        int changed = 0;
        for (int i = 0; i < res.Values.Count && i < item.Slots.Count; i++)
        {
            int slot = item.Slots[i];
            if (res.Values[i] != ctx.Current[i])
            {
                if (_slotVals is not null && slot < _slotVals.Length) _slotVals[slot] = res.Values[i];
                SlotMappingChanged?.Invoke(slot, res.Values[i].Code, res.Values[i].Mod);
                changed++;
            }
            if (res.HasEnabled && i < res.Enabled.Count && res.Enabled[i] != ctx.Enabled[i])
            {
                if (_slotOn is not null && slot < _slotOn.Length) _slotOn[slot] = res.Enabled[i];
                SlotEnabledChanged?.Invoke(slot, res.Enabled[i]);
                changed++;
            }
        }

        if (res.HasStick && stickSettings
            && (res.DeadzonePct != ctx.DeadzonePct
                || res.InvertX != ctx.InvertX || res.InvertY != ctx.InvertY))
        {
            _stickDeadzone[stickIdx] = res.DeadzonePct;
            _stickInvX[stickIdx] = res.InvertX;
            _stickInvY[stickIdx] = res.InvertY;
            StickSettingsChanged?.Invoke(stickIdx, res.DeadzonePct, res.InvertX, res.InvertY);
            changed++;
        }

        if (changed == 0) return;

        // 立刻重画映射牌。_cfg 是被 MainForm 改的（共享实例），
        // 所以这里重新读一遍就能看到新值。
        RefreshSlotText();
        SetLayoutStatus($"已修改 {item.Name} 的 {changed} 项设置 —— 点上方「保存并重启」写入设备",
                        DarkTheme.Warn);
    }
}
