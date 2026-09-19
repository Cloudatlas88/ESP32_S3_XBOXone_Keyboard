using KbConfigurator.Model;
using KbConfigurator.Protocol;
using KbConfigurator.Ui;

namespace KbConfigurator.Views;

/// <summary>弹面板要显示 / 返回的全部内容。</summary>
internal sealed class PopupContext
{
    public LayoutItem Item = null!;

    /// <summary>每个槽位当前的 (键码, 修饰键)</summary>
    public List<(byte Code, byte Mod)> Current = new();

    /// <summary>每个槽位在当前固件下能不能改</summary>
    public List<bool> Editable = new();

    /// <summary>每个槽位是否启用（只有单槽位项显示这个开关）</summary>
    public List<bool> Enabled = new();

    /// <summary>每个槽位下面的只读说明（例如"已绑定宏 2，本行映射被忽略"）</summary>
    public List<string> Notes = new();

    // ── 摇杆专用（只在摇杆项上显示）──
    public bool ShowStickSettings;
    public int DeadzonePct = 12;
    public bool InvertX, InvertY;
}

/// <summary>弹面板的返回值。null = 取消。</summary>
internal sealed class PopupResult
{
    public List<(byte Code, byte Mod)> Values = new();
    public List<bool> Enabled = new();
    public bool HasEnabled;

    public bool HasStick;
    public int DeadzonePct;
    public bool InvertX, InvertY;
}

/// <summary>
/// 点图上某个热区后弹出的映射编辑面板。
///
/// ★ 为什么用**弹出面板**而不是在图上常驻 15 个下拉框：
///   图是要等比缩放的，窗口一小每个标签框只剩几十像素宽，
///   常驻下拉会互相重叠、被裁掉，而且把本就密集的图盖得没法看。
///   弹面板同一时刻只有一个，位置跟着点击点走，缩放到多小都不会乱。
///   （设计文档 §3.2 原本画的是常驻下拉，这是实现时按"缩放后可用性"改的。）
///
/// ★ 单键：一行下拉（+ 启用开关）。方向类：4 个下拉按十字摆，
///   形状本身就暗示方向，不用看图例。摇杆项额外带死区 / X、Y 反向
///   —— 这两项原来在旧「按键映射」页，合并时搬到这里。
/// </summary>
internal sealed class SlotMapPopup : Form
{
    private readonly List<ComboBox> _combos = new();
    private readonly List<CheckBox> _enabled = new();
    private NumericUpDown? _deadzone;
    private CheckBox? _invertX, _invertY;
    private readonly PopupContext _ctx;
    private bool _ok;

    private SlotMapPopup(PopupContext ctx)
    {
        _ctx = ctx;
        var item = ctx.Item;

        Text = item.Name;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        MaximizeBox = false;
        MinimizeBox = false;
        KeyPreview = true;
        BackColor = DarkTheme.FormBack;
        ForeColor = DarkTheme.Text;
        Font = DarkTheme.Ui(9f);
        AutoScaleMode = AutoScaleMode.Font;
        Padding = new Padding(10, 8, 10, 8);

        bool anyEditable = ctx.Editable.Any(e => e);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = DarkTheme.FormBack,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        L.AddRow(root, L.Text(item.Name, DarkTheme.Text, bold: true, size: 10f));
        if (item.DiagramLabel.Length > 0)
            L.AddRow(root, L.Header("图上原文：" + item.DiagramLabel));

        if (item.IsDir4) L.AddRow(root, BuildCross());
        else L.AddRow(root, BuildSingleRow());

        if (ctx.ShowStickSettings) L.AddRow(root, BuildStickSettings(), 8);

        if (!anyEditable)
        {
            L.AddRow(root, L.Text(
                "⚠ 这些槽位在当前固件里没有存储位置，暂时只能查看。\n" +
                "   （此提示仅在固件版本过旧、该槽位无对应字段时出现。）",
                DarkTheme.Warn, size: 8.5f), 8);
        }

        L.AddRow(root, BuildButtons(), 10);

        Controls.Add(root);
        DarkTheme.Apply(this);

        // ★★ 高度也必须设，而且要用 ClientSize。
        //   原实现只设了 Width —— 窗体高度就停在默认的 300px，
        //   一旦内容（尤其是"摇杆设置"那一块）超过 300，
        //   底部的「确定 / 取消 / 清除映射」就被切到可视区外，
        //   用户看到的是"保存按钮都看不见"（截图里只露出按钮的上边缘）。
        //   用 ClientSize 而不是 Width/Height：后者含边框和标题栏，
        //   而 root 是 Dock=Fill、填的是**客户区**。
        var need = root.PreferredSize;
        int w = Math.Clamp(need.Width + Padding.Horizontal + 8, 300, 680);
        int h = need.Height + Padding.Vertical + 8;

        // 内容再多也别超出屏幕（超出就把高度收到屏幕内，剩下的靠标题栏拖不动也没关系，
        // 目前最长的那一档离屏幕高度还有很大余量，这里只是兜底）
        var work = Screen.FromPoint(Cursor.Position).WorkingArea;
        h = Math.Min(h, work.Height - 60);

        ClientSize = new Size(w, h);
    }

    private bool AnyEnabledCombo => _combos.Any(c => c.Enabled);

    /// <summary>单键：一行「下拉 + 启用开关」</summary>
    private Control BuildSingleRow()
    {
        var item = _ctx.Item;
        int slot = item.Slots[0];

        var row = L.Grid(-1, 92);
        row.AutoSize = true;
        row.RowCount = 2;
        L.RowHeight(row, 0, 24);
        L.RowHeight(row, 1, 20);

        var cb = KeyCombo.Make();
        cb.Enabled = _ctx.Editable[0];
        cb.Dock = DockStyle.Fill;
        KeyCombo.SelectByKey(cb, _ctx.Current[0].Code, _ctx.Current[0].Mod);
        _combos.Add(cb);
        row.Controls.Add(cb, 0, 0);

        // 启用开关：不加这个的话用户只能选"不映射"，而那是**丢掉键码**，
        // 和"暂时禁用但保留映射"是两回事。
        bool canEnable = _ctx.Editable[0] && SlotMap.SlotSupportsEnable(slot);
        var chk = new CheckBox
        {
            Text = "启用",
            Checked = _ctx.Enabled.Count > 0 && _ctx.Enabled[0],
            Enabled = canEnable,
            AutoSize = false,
            Dock = DockStyle.Fill,
            ForeColor = DarkTheme.Text,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(6, 0, 0, 0),
        };
        chk.FlatAppearance.BorderColor = DarkTheme.Border;
        chk.FlatAppearance.CheckedBackColor = DarkTheme.Accent;
        _enabled.Add(chk);
        row.Controls.Add(chk, 1, 0);

        string note = _ctx.Notes.Count > 0 ? _ctx.Notes[0] : "";
        if (note.Length == 0) note = $"槽位 {slot}" + (_ctx.Editable[0] ? "" : "（不可编辑）");
        row.Controls.Add(L.Header(note), 0, 1);
        row.SetColumnSpan(row.GetControlFromPosition(0, 1)!, 2);

        return row;
    }

    /// <summary>方向类：4 个下拉按十字摆</summary>
    private Control BuildCross()
    {
        var item = _ctx.Item;
        var arrow = new[] { "↑ 上", "↓ 下", "← 左", "→ 右" };
        // 十字位置：上(1,0) 左(0,1) 中(1,1) 右(2,1) 下(1,2)
        var pos = new[] { (1, 0), (1, 2), (0, 1), (2, 1) };

        var grid = L.Grid(150, 96, 150);
        grid.RowCount = 3;
        for (int r = 0; r < 3; r++) L.RowHeight(grid, r, 46);

        for (int i = 0; i < 4 && i < item.Slots.Count; i++)
        {
            var cb = KeyCombo.Make();
            cb.Enabled = _ctx.Editable[i];
            cb.Dock = DockStyle.Fill;
            KeyCombo.SelectByKey(cb, _ctx.Current[i].Code, _ctx.Current[i].Mod);
            _combos.Add(cb);

            // ★ 箭头文字和下拉**不能塞进同一个单元格** —— 那会两层叠在一起。
            //   每个格子自己是个"标题 + 下拉"的两行小堆叠。
            grid.Controls.Add(Stacked(arrow[i], cb), pos[i].Item1, pos[i].Item2);
        }

        var mid = L.Header("方向");
        mid.TextAlign = ContentAlignment.MiddleCenter;
        mid.Dock = DockStyle.Fill;
        grid.Controls.Add(mid, 1, 1);

        return grid;
    }

    /// <summary>摇杆专用：死区 + X / Y 反向（原来在旧「按键映射」页）</summary>
    private Control BuildStickSettings()
    {
        var row = L.Grid(96, 92, -1, -1);
        row.AutoSize = true;
        row.RowCount = 2;
        L.RowHeight(row, 0, 26);
        L.RowHeight(row, 1, 22);

        row.Controls.Add(L.Header("摇杆死区 %"), 0, 0);

        _deadzone = new NumericUpDown
        {
            Minimum = 0, Maximum = 60, Value = Math.Clamp(_ctx.DeadzonePct, 0, 60),
            Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0),
        };
        row.Controls.Add(_deadzone, 1, 0);

        _invertX = new CheckBox
        {
            Text = "X 轴反向", Checked = _ctx.InvertX, AutoSize = false,
            Dock = DockStyle.Fill, ForeColor = DarkTheme.Text, FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0),
        };
        _invertY = new CheckBox
        {
            Text = "Y 轴反向", Checked = _ctx.InvertY, AutoSize = false,
            Dock = DockStyle.Fill, ForeColor = DarkTheme.Text, FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0),
        };
        foreach (var c in new[] { _invertX, _invertY })
        {
            c.FlatAppearance.BorderColor = DarkTheme.Border;
            c.FlatAppearance.CheckedBackColor = DarkTheme.Accent;
        }
        row.Controls.Add(_invertX, 2, 0);
        row.Controls.Add(_invertY, 3, 0);

        row.Controls.Add(L.Header("死区越小越灵敏，抖动大的旧摇杆要调大"), 0, 1);
        row.SetColumnSpan(row.GetControlFromPosition(0, 1)!, 4);

        return row;
    }

    /// <summary>一个"小标题 + 控件"的竖排堆叠</summary>
    private static Control Stacked(string caption, Control inner)
    {
        var t = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 2,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 1, 4, 1),
            BackColor = DarkTheme.FormBack,
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));

        var cap = L.Header(caption);
        cap.Dock = DockStyle.Fill;
        cap.TextAlign = ContentAlignment.MiddleLeft;
        t.Controls.Add(cap, 0, 0);
        t.Controls.Add(inner, 0, 1);
        return t;
    }

    private Control BuildButtons()
    {
        var bar = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = DarkTheme.FormBack,
            Margin = new Padding(0),
        };

        bool anyEditable = AnyEnabledCombo || _enabled.Any(c => c.Enabled)
                        || (_deadzone is not null);
        bool canEdit = AnyEnabledCombo || _enabled.Any(c => c.Enabled);

        var ok = L.Button(canEdit ? "确定" : "关闭", 84, DarkTheme.Accent);
        var cancel = L.Button("取消", 84, null);
        var clear = L.Button("清除映射", 96, null);

        ok.Click += (_, _) => { _ok = true; Close(); };
        cancel.Click += (_, _) => Close();
        clear.Click += (_, _) =>
        {
            // 清除 = 所有槽位都设成"不映射"（0,0），不是一个一个手动选
            foreach (var cb in _combos)
                if (cb.Enabled) KeyCombo.SelectByKey(cb, 0, 0);
        };

        cancel.Enabled = anyEditable;
        clear.Enabled = AnyEnabledCombo;

        bar.Controls.Add(ok);
        bar.Controls.Add(cancel);
        bar.Controls.Add(clear);
        return bar;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // ★ 焦点不要落在第一个下拉上：ComboBox 获得焦点会**自动全选**文本，
        //   静止状态下整条显示成蓝色高亮，看着像"这一项被选中了"，
        //   而不是"当前值是这个"。多个下拉并排时尤其乱。
        //   （给 Text 赋值也会顺手全选，所以这里再清一次选中。）
        ActiveControl = null;
        foreach (var cb in _combos)
        {
            cb.SelectionStart = cb.Text.Length;
            cb.SelectionLength = 0;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) { _ok = false; Close(); }
        else if (e.KeyCode == Keys.Enter && AnyEnabledCombo) { _ok = true; Close(); }
    }

    /// <summary>
    /// 测试用入口：直接构造面板以便检查它建了哪些控件、状态对不对。
    /// 正常路径走 <see cref="Edit"/>，那会弹模态对话框，测试里没法用。
    /// </summary>
    internal static SlotMapPopup CreateForTest(PopupContext ctx) => new(ctx);

    /// <summary>面板里的下拉框（顺序与 item.Slots 一致），测试用</summary>
    internal IReadOnlyList<ComboBox> Combos => _combos;

    /// <summary>面板里的启用开关，测试用</summary>
    internal IReadOnlyList<CheckBox> EnabledBoxes => _enabled;

    internal NumericUpDown? DeadzoneBox => _deadzone;

    /// <summary>
    /// 弹出面板编辑一组槽位。
    /// 返回 null 表示取消；否则返回每个槽位的新 (键码, 修饰键) 与其它设置。
    /// </summary>
    public static PopupResult? Edit(IWin32Window? owner, Rectangle hotspotScreenRect,
                                    PopupContext ctx)
    {
        using var dlg = new SlotMapPopup(ctx);

        // 贴着热区右上角弹；超出屏幕就翻到另一侧
        var scr = Screen.FromRectangle(hotspotScreenRect).WorkingArea;
        int x = hotspotScreenRect.Right + 8;
        int y = hotspotScreenRect.Top - 8;
        if (x + dlg.Width > scr.Right) x = Math.Max(scr.Left, hotspotScreenRect.Left - dlg.Width - 8);
        if (y + dlg.Height > scr.Bottom) y = Math.Max(scr.Top, scr.Bottom - dlg.Height - 8);
        dlg.Location = new Point(x, y);

        dlg.ShowDialog(owner);
        if (!dlg._ok) return null;

        var res = new PopupResult();
        foreach (var cb in dlg._combos)
            res.Values.Add((KeyCombo.SelectedCode(cb), KeyCombo.SelectedMod(cb)));

        if (dlg._enabled.Count > 0)
        {
            res.HasEnabled = true;
            foreach (var chk in dlg._enabled) res.Enabled.Add(chk.Checked);
        }

        if (dlg._deadzone is not null)
        {
            res.HasStick = true;
            res.DeadzonePct = (int)dlg._deadzone.Value;
            res.InvertX = dlg._invertX?.Checked ?? false;
            res.InvertY = dlg._invertY?.Checked ?? false;
        }

        return res;
    }
}
