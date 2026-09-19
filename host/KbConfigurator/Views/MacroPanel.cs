using KbConfigurator.Model;
using KbConfigurator.Protocol;
using KbConfigurator.Ui;

namespace KbConfigurator.Views;

/// <summary>
/// 宏编辑页。
///
/// ★ 本次改造点：
///   · 布局改成 TableLayoutPanel（原来宏属性组 104px 高，底下的说明文字
///     已经溢出到框外了，被 --layout 检查抓出来）；
///   · **左侧宏列表**：原来只有一个下拉，看不出"一共几个宏、哪些是空的、
///     哪些绑了按钮"。现在列表每项显示 步数 / ★循环 / 绑定按钮；
///   · **步骤可增删排序**：以前用"连续勾选框"表达有效步数 ——
///     想删掉第 2 步得把后面全取消再重勾，非常绕。
///     现在每行有 [↑][↓][✕]，`step_count` 就是行数。
///
/// ★ 触发绑定下拉**保留可编辑**（与改造前一致）：
///   按键映射页那侧只显示说明文字，真正的绑定在这里改。
/// </summary>
public sealed class MacroPanel : UserControl, IConfigEditor
{
    private const int StepRows = KbConfig.MacroStepMax;

    /// <summary>步骤列表一次显示几行（其余靠滚动看）—— 20 步全摊开会把整页撑爆</summary>
    private const int StepVisibleRows = 9;

    /// <summary>单行高度（32 行高 + 上下各 1px 边距）</summary>
    private const int StepRowPx = 34;

    private ScrollableControl? _stepScroll;

    // ── 左：宏列表 ──
    private readonly ListBox _macroList = new()
    {
        Dock = DockStyle.Fill,
        IntegralHeight = false,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = DarkTheme.InputBack,
        ForeColor = DarkTheme.Text,
        Font = DarkTheme.Mono(9.5f),
        ItemHeight = 22,
    };
    private readonly Button _btnClear = L.Button("清空本宏", 96);

    // ── 右：宏属性 ──
    private readonly NumericUpDown _timeout = new()
    {
        Minimum = 0, Maximum = 65535, Width = 90, Increment = 1000,
    };
    private readonly CheckBox _loop = new()
    {
        Text = "循环执行（触发按钮 = 开 / 停 开关）", AutoSize = true,
    };
    private readonly ComboBox _triggerBtn = new()
    {
        Width = 170, DropDownStyle = ComboBoxStyle.DropDownList,
    };

    // ── 右：步骤表 ──
    private readonly List<StepRow> _rows = new();

    /// <summary>设备上宏的运行状态（原「测试」分组里的那行文字，现在挪到列表标题旁）</summary>
    private readonly Label _liveState = L.Text("设备未连接", DarkTheme.TextDim);

    private KbConfig _model = KbConfig.CreateDefault();
    private int _curMacro;
    private bool _loading;      // 载入界面期间不要回写模型

    /// <summary>
    /// 当前宏的步数（1~8，0 = 空宏）。
    ///
    /// ★ 显式记这个数，而不是"看哪几行有内容"去推断：
    ///   推断会在往返时把合法的"空步"截掉（比如某步动作就是标准值），
    ///   改一次保存一次步数就少一截。显式计数 + 增删时维护才是对的。
    /// </summary>
    private int _stepCount;

    /// <summary>点「测试触发」/「中止」时由 MainForm 接上真实设备操作（0xFF = 中止）</summary>
    // RequestRunMacro 事件已随「测试触发 / 中止」按钮一起删除 —— 不再需要。


    /// <summary>一个步骤行包含的控件</summary>
    private sealed class StepRow
    {
        public required ComboBox Action { get; init; }
        public required ComboBox Key { get; init; }
        public required NumericUpDown Val1 { get; init; }
        public required NumericUpDown Val2 { get; init; }
        public required Button Up { get; init; }
        public required Button Down { get; init; }
        public required Button Del { get; init; }
        public required TableLayoutPanel Host { get; init; }
    }

    public MacroPanel()
    {
        Dock = DockStyle.Fill;
        Padding = new Padding(12);
        BackColor = DarkTheme.FormBack;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = DarkTheme.FormBack,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        root.Controls.Add(BuildMacroList(), 0, 0);
        root.Controls.Add(BuildEditor(), 1, 0);

        Controls.Add(root);

        BuildStepRows();
        RefreshMacroList();
        LoadMacroToUi();

        DarkTheme.Apply(this);
    }

    // ══════════════════════════════════════════════════════════════
    //  左：宏列表
    // ══════════════════════════════════════════════════════════════

    private Control BuildMacroList()
    {
        var stack = L.Stack();

        // 标题行：左边"宏列表"，右边设备上宏的运行状态
        // （原来这两样分在两个地方，开着「测试」分组占了一整块地方）
        var head = L.Grid(-1, -1);
        var title = L.Header("宏列表");
        title.Anchor = AnchorStyles.Left;
        L.Cell(head, title, 0, 0);
        _liveState.Margin = new Padding(0);
        L.Cell(head, _liveState, 1, 0, 0);
        L.RowHeight(head, 0, 22);
        L.AddRow(stack, head);

        var listHost = new Panel { Dock = DockStyle.Fill, Height = 220, Margin = new Padding(0, 4, 8, 6) };
        listHost.Controls.Add(_macroList);
        L.AddRow(stack, listHost);
        L.MakeGrow(stack, stack.RowStyles.Count - 1);

        _macroList.SelectedIndexChanged += (_, _) =>
        {
            if (_loading) return;
            if (_macroList.SelectedIndex < 0) return;
            SaveCurrentMacroToModel();
            _curMacro = _macroList.SelectedIndex;
            LoadMacroToUi();
        };

        _btnClear.Margin = new Padding(0, 0, 8, 0);
        _btnClear.Click += (_, _) => ClearCurrentMacro();

        var bottom = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
        };
        bottom.Controls.Add(_btnClear);
        L.AddRow(stack, bottom, 4);

        return L.Group("宏", stack);
    }

    private void RefreshMacroList()
    {
        int keep = _curMacro;

        _loading = true;
        _macroList.BeginUpdate();
        _macroList.Items.Clear();

        for (int m = 0; m < KbConfig.MacroMax; m++)
        {
            var def = _model.Macros[m];
            string desc = def.StepCount == 0 ? "（空）" : $"{def.StepCount} 步";
            string loop = def.Loop ? " ★循环" : "";

            string bind = "未绑定";
            for (int i = 0; i < KbConfig.SlotCount; i++)
                if (_model.Buttons[i].UsesMacro && _model.Buttons[i].MacroId == m)
                {
                    bind = $"绑定 {SlotMap.NameOf(i)}";
                    break;
                }

            _macroList.Items.Add($"宏{m + 1}  {desc}{loop}    {bind}");
        }

        _macroList.SelectedIndex = Math.Clamp(keep, 0, KbConfig.MacroMax - 1);
        _macroList.EndUpdate();
        _loading = false;
    }

    // ══════════════════════════════════════════════════════════════
    //  右：宏属性 + 步骤
    // ══════════════════════════════════════════════════════════════

    private Control BuildEditor()
    {
        var stack = L.Stack();
        L.AddRow(stack, BuildPropGroup(), 0);
        L.AddRow(stack, BuildStepGroup(), 8);
        return L.Scroll(stack);
    }

    private Control BuildPropGroup()
    {
        var g = L.Grid(84, 104, -1, 84, -1);

        L.Cell(g, L.Text("总超时(ms)："), 0, 0);
        L.Cell(g, _timeout, 1, 0);
        L.Cell(g, L.Text("0 = 用固件默认 30000", DarkTheme.TextDim), 2, 0);
        L.RowHeight(g, 0, 32);

        L.Cell(g, L.Text("触发绑定："), 0, 1);
        _triggerBtn.Items.Add("（不触发）");
        foreach (var n in SlotMap.SlotNames) _triggerBtn.Items.Add(n);
        _triggerBtn.SelectedIndex = 0;
        L.Cell(g, _triggerBtn, 1, 1);
        L.Cell(g, L.Text("「循环执行」时：按一下开始、再按一下停止", DarkTheme.TextDim), 2, 1);
        L.RowHeight(g, 1, 32);

        _loop.Margin = new Padding(0, 6, 0, 0);
        L.Cell(g, _loop, 0, 2);
        g.SetColumnSpan(_loop, 3);
        L.RowHeight(g, 2, 30);

        return L.Group("宏属性", g);
    }

    private Control BuildStepGroup()
    {
        var body = L.Stack();

        // 表头
        var head = L.Grid(30, 128, -1, 96, 86, 96);
        L.Cell(head, L.Header("#"), 0, 0);
        L.Cell(head, L.Header("动作"), 1, 0);
        L.Cell(head, L.Header("目标按键"), 2, 0);
        L.Cell(head, L.Header("延迟/基准(ms)"), 3, 0);
        L.Cell(head, L.Header("抖动(%)"), 4, 0);
        L.Cell(head, L.Header("操作"), 5, 0);
        L.RowHeight(head, 0, 24);
        L.AddRow(body, head);

        // 步骤行容器
        var rowsHost = new TableLayoutPanel
        {
            ColumnCount = 1,
            // ★ Dock=Top 而不是 Fill：它要放在下面的滚动区里，
            //   高度由内容（行数）决定，超出滚动区才出现滚动条。
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = DarkTheme.PanelBack,
            Margin = new Padding(0),
        };
        rowsHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        // ★ 固定高度 + 自动滚动：20 步只显示前 9 行，其余滚轮/拖滚动条看。
        //   原来只建 8 行控件，第 9 步连控件都没有 —— 所以"加到第 9 步不显示"。
        _stepScroll = new Panel
        {
            Dock = DockStyle.Top,
            Height = StepVisibleRows * StepRowPx + 4,
            AutoScroll = true,
            BackColor = DarkTheme.PanelBack,
            Margin = new Padding(0),
        };
        _stepScroll.Controls.Add(rowsHost);
        L.AddRow(body, _stepScroll);

        // 「加入步骤」按钮
        var add = L.Button("＋ 加入步骤", 110);
        add.Click += (_, _) => AddStep();
        var addRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 6, 0, 0),
        };
        addRow.Controls.Add(add);
        addRow.Controls.Add(L.Text("↑ ↓ 调整顺序，✕ 删除；从上往下依次执行", DarkTheme.TextDim));
        L.AddRow(body, addRow);

        _stepHost = rowsHost;
        return L.Group("宏步骤", body);
    }

    private TableLayoutPanel? _stepHost;

    /// <summary>
    /// 测试/诊断用：触发绑定下拉框 —— 看它到底列了哪些按键。
    /// 用户报的就是这里列的内容不对，先把实际内容打出来再改，别凭猜。
    /// </summary>
    internal ComboBox TriggerBindingForTest => _triggerBtn;

    /// <summary>
    /// 测试用：步骤列表的滚动容器。
    /// 有了它才能断言"装满 20 步时内容高于可视区"和"滚轮真的滚得动" ——
    /// 否则这两件事只能靠肉眼，我改完也只能说"应该能滚吧"。
    /// </summary>
    internal ScrollableControl? StepScrollForTest => _stepScroll;

    /// <summary>测试用：一次显示几行</summary>
    internal static int StepVisibleRowsForTest => StepVisibleRows;

    // 「测试触发 / 中止」整组已按需求删除。
    // 宏的运行状态仍然显示（挪到左侧「宏列表」标题旁），
    // 想验证一个宏，直接按它绑定的物理按钮即可 —— 那才是真实使用路径。

    // ══════════════════════════════════════════════════════════════
    //  步骤行
    // ══════════════════════════════════════════════════════════════

    private void BuildStepRows()
    {
        for (int i = 0; i < StepRows; i++) CreateRow();
        RelayoutStepRows();
    }

    private StepRow CreateRow()
    {
        var act = new WheelCombo
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0, 2, 6, 2),
        };
        act.Items.AddRange(KbConfig.ActionNames.Cast<object>().ToArray());
        act.SelectedIndex = 0;

        var key = new WheelCombo
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0, 2, 6, 2),
        };
        key.Items.AddRange(KeyCodes.All.Cast<object>().ToArray());
        key.SelectedIndex = 0;

        var v1 = new WheelNumeric
        {
            Minimum = 0, Maximum = 65535, Width = 90, Increment = 10, Value = 200,
            Anchor = AnchorStyles.Left, Margin = new Padding(0, 2, 6, 2),
        };
        var v2 = new WheelNumeric
        {
            Minimum = 0, Maximum = KbConfig.MaxJitterPct, Width = 80, Increment = 5, Value = 30,
            Anchor = AnchorStyles.Left, Margin = new Padding(0, 2, 6, 2),
        };

        var up = L.TinyButton("↑", "上移");
        var dn = L.TinyButton("↓", "下移");
        var del = L.TinyButton("✕", "删除这一步");
        up.Width = dn.Width = del.Width = 24;

        var ops = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
        };
        ops.Controls.Add(up);
        ops.Controls.Add(dn);
        ops.Controls.Add(del);

        var row = new StepRow
        {
            Action = act, Key = key, Val1 = v1, Val2 = v2,
            Up = up, Down = dn, Del = del, Host = L.Grid(30, 128, -1, 96, 86, 96),
        };

        act.SelectedIndexChanged += (_, _) => SyncRow(row);

        up.Click += (_, _) => MoveStep(row, -1);
        dn.Click += (_, _) => MoveStep(row, +1);
        del.Click += (_, _) => DeleteStep(row);

        _rows.Add(row);
        SyncRow(row);
        return row;
    }

    /// <summary>
    /// 会把滚轮"让"给外层滚动区的 ComboBox。
    /// ComboBox 是原生控件，自己吃掉 WM_MOUSEWHEEL 去改选中项、不会往上传，
    /// 于是"鼠标停在步骤行上滚轮翻页"完全没反应 —— 而这正是要的功能。
    /// </summary>
    private sealed class WheelCombo : ComboBox
    {
        public ScrollableControl? WheelTarget;
        protected override void WndProc(ref Message m)
        {
            if (WheelForward.TryForward(m, WheelTarget)) return;
            base.WndProc(ref m);
        }
    }

    /// <summary>同上，NumericUpDown 默认会拿滚轮改数值。</summary>
    private sealed class WheelNumeric : NumericUpDown
    {
        public ScrollableControl? WheelTarget;
        protected override void WndProc(ref Message m)
        {
            if (WheelForward.TryForward(m, WheelTarget)) return;
            base.WndProc(ref m);
        }
    }

    /// <summary>把 WM_MOUSEWHEEL 换算成滚动区的偏移量</summary>
    private static class WheelForward
    {
        private const int WM_MOUSEWHEEL = 0x020A;

        /// <summary>一格滚轮滚多少像素（两行左右，翻起来不费劲也不失控）</summary>
        private const int PerNotchPx = 2 * StepRowPx;

        public static bool TryForward(Message m, ScrollableControl? target)
        {
            if (m.Msg != WM_MOUSEWHEEL || target is null || target.Controls.Count == 0)
                return false;

            int delta = (short)(((long)m.WParam >> 16) & 0xFFFF);
            if (delta == 0) return false;

            int content = target.Controls[0].Height + target.Controls[0].Margin.Vertical;
            int max = Math.Max(0, content - target.ClientSize.Height);
            int cur = -target.AutoScrollPosition.Y;
            int next = Math.Clamp(cur - Math.Sign(delta) * PerNotchPx, 0, max);

            target.AutoScrollPosition = new Point(0, next);   // 注意：设的是**负**偏移
            return true;
        }
    }

    /// <summary>按当前步数重排步骤行（只显示有内容的那几行 + 一个空行供添加）</summary>
    private void RelayoutStepRows()
    {
        if (_stepHost is null) return;

        _stepHost.SuspendLayout();
        _stepHost.Controls.Clear();
        _stepHost.RowStyles.Clear();
        _stepHost.RowCount = 0;

        int used = CurrentStepCount();
        int show = Math.Min(StepRows, used + 1);      // 多显示一个空行，方便"再加一步"

        for (int i = 0; i < show; i++)
        {
            var r = _rows[i];

            // 把这些行控件收到的滚轮转给外层滚动区（否则它们会自己吃掉）
            if (r.Action is WheelCombo wa) wa.WheelTarget = _stepScroll;
            if (r.Key    is WheelCombo wk) wk.WheelTarget = _stepScroll;
            if (r.Val1   is WheelNumeric w1) w1.WheelTarget = _stepScroll;
            if (r.Val2   is WheelNumeric w2) w2.WheelTarget = _stepScroll;
            r.Host.SuspendLayout();
            r.Host.Controls.Clear();
            r.Host.RowStyles.Clear();
            r.Host.RowCount = 1;
            r.Host.ColumnCount = 6;

            var idx = L.Text($"{i + 1}", DarkTheme.TextDim);
            idx.Anchor = AnchorStyles.Left;
            idx.Margin = new Padding(2, 6, 0, 0);
            L.Cell(r.Host, idx, 0, 0);
            L.Cell(r.Host, r.Action, 1, 0, 4);
            L.Cell(r.Host, r.Key, 2, 0, 4);
            L.Cell(r.Host, r.Val1, 3, 0, 4);
            L.Cell(r.Host, r.Val2, 4, 0, 4);
            L.Cell(r.Host, BuildOps(r, i, used), 5, 0, 2);

            r.Host.ResumeLayout();

            r.Host.Dock = DockStyle.Fill;
            r.Host.Margin = new Padding(0, 1, 0, 1);
            _stepHost.Controls.Add(r.Host, 0, _stepHost.RowCount);
            _stepHost.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            _stepHost.RowCount++;
        }

        _stepHost.ResumeLayout();

        // ★ 滚动区高度跟着内容收缩，**最多** StepVisibleRows 行。
        //   固定高度在步骤少的时候会在下面留一大块空白，看着像没做完；
        //   而超过 9 行就必须限高 + 滚动（20 行全摊开 680px 会把整页撑爆）。
        if (_stepScroll is not null)
            _stepScroll.Height = Math.Min(StepVisibleRows, show) * StepRowPx + 4;
    }

    private Control BuildOps(StepRow r, int index, int used)
    {
        var ops = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
        };
        ops.Controls.Add(r.Up);
        ops.Controls.Add(r.Down);
        ops.Controls.Add(r.Del);

        // 首行不能上移、末行不能下移
        r.Up.Enabled = index > 0;
        r.Down.Enabled = index < used - 1;
        return ops;
    }

    /// <summary>界面当前显示的步数（就是 <see cref="_stepCount"/>）</summary>
    private int CurrentStepCount() => _stepCount;

    private void AddStep()
    {
        if (_stepCount >= StepRows) return;
        _stepCount++;

        // 新加的那一行给个合理默认值，免得是"空动作"让人一脸茫然
        var r = _rows[_stepCount - 1];
        if (r.Action.SelectedIndex == 0 && r.Key.SelectedIndex == 0 && r.Val1.Value == 0)
            r.Val1.Value = 200;

        RelayoutStepRows();
    }

    private void DeleteStep(StepRow row)
    {
        int i = _rows.IndexOf(row);
        if (i < 0 || i >= _stepCount) return;

        // 从第 i 行开始，把后面的往前挪一格
        for (int j = i; j < _rows.Count - 1; j++) CopyRow(_rows[j + 1], _rows[j]);
        ClearRow(_rows[^1]);

        _stepCount--;
        RelayoutStepRows();
    }

    private void MoveStep(StepRow row, int delta)
    {
        int i = _rows.IndexOf(row);
        int j = i + delta;
        if (i < 0 || j < 0 || i >= _stepCount || j >= _stepCount) return;

        SwapRows(_rows[i], _rows[j]);
        RelayoutStepRows();
    }

    private static void CopyRow(StepRow from, StepRow to)
    {
        to.Action.SelectedIndex = from.Action.SelectedIndex;
        to.Key.SelectedIndex    = from.Key.SelectedIndex;
        to.Val1.Value           = from.Val1.Value;
        to.Val2.Value           = from.Val2.Value;
    }

    private static void SwapRows(StepRow a, StepRow b)
    {
        (a.Action.SelectedIndex, b.Action.SelectedIndex) = (b.Action.SelectedIndex, a.Action.SelectedIndex);
        (a.Key.SelectedIndex,    b.Key.SelectedIndex)    = (b.Key.SelectedIndex,    a.Key.SelectedIndex);
        (a.Val1.Value,           b.Val1.Value)           = (b.Val1.Value,           a.Val1.Value);
        (a.Val2.Value,           b.Val2.Value)           = (b.Val2.Value,           a.Val2.Value);
    }

    private static void ClearRow(StepRow r)
    {
        r.Action.SelectedIndex = 0;
        r.Key.SelectedIndex = 0;
        r.Val1.Value = 0;
        r.Val2.Value = 0;
    }

    /// <summary>按动作类型灰掉用不到的控件</summary>
    private static void SyncRow(StepRow r)
    {
        var a = (byte)r.Action.SelectedIndex;
        bool isDelay = a is KbConfig.ActDelay or KbConfig.ActRandDelay;
        bool isRand  = a == KbConfig.ActRandDelay;

        r.Key.Enabled  = !isDelay;
        r.Val1.Enabled = isDelay;
        r.Val2.Enabled = isRand;
    }

    private void ClearCurrentMacro()
    {
        if (MessageBox.Show($"清空「宏{_curMacro + 1}」的所有步骤？", "清空宏",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;

        foreach (var r in _rows) ClearRow(r);
        _stepCount = 0;
        _model.Macros[_curMacro].StepCount = 0;
        _model.Macros[_curMacro].Loop = false;
        _loop.Checked = false;
        RelayoutStepRows();
        RefreshMacroList();
    }

    // ══════════════════════════════════════════════════════════════
    //  IConfigEditor
    // ══════════════════════════════════════════════════════════════

    public void LoadFromModel(KbConfig c)
    {
        _model = c;
        _curMacro = Math.Clamp(_curMacro, 0, KbConfig.MacroMax - 1);
        RefreshMacroList();
        LoadMacroToUi();
    }

    public void WriteToModel(KbConfig c)
    {
        _model = c;
        SaveCurrentMacroToModel();

        int used = 0;
        for (int m = 0; m < KbConfig.MacroMax; m++)
            if (c.Macros[m].StepCount > 0) used = m + 1;
        c.MacroCount = (byte)used;
    }

    private void LoadMacroToUi()
    {
        _loading = true;
        try
        {
            int idx = Math.Clamp(_curMacro, 0, KbConfig.MacroMax - 1);
            var m = _model.Macros[idx];

            _timeout.Value = Math.Clamp((int)m.TimeoutMs, 0, 65535);
            _loop.Checked = m.Loop;

            int bind = -1;
            for (int i = 0; i < KbConfig.SlotCount; i++)
                if (_model.Buttons[i].UsesMacro && _model.Buttons[i].MacroId == idx) { bind = i; break; }
            _triggerBtn.SelectedIndex = bind + 1;

            foreach (var r in _rows) ClearRow(r);

            // 步数直接取自配置，不靠"哪几行有内容"推断
            _stepCount = Math.Clamp((int)m.StepCount, 0, StepRows);

            for (int i = 0; i < _stepCount; i++)
            {
                var st = m.Steps[i];
                var r = _rows[i];
                r.Action.SelectedIndex = Math.Clamp((int)st.Action, 0, KbConfig.ActionNames.Length - 1);
                SelectByKey(r.Key, st.Keycode, st.Modifiers);
                r.Val1.Value = Math.Clamp((int)st.DelayMinMs, 0, 65535);
                r.Val2.Value = Math.Clamp((int)st.DelayMaxMs, 0, KbConfig.MaxJitterPct);
                SyncRow(r);
            }
        }
        finally { _loading = false; }

        RelayoutStepRows();
    }

    private void SaveCurrentMacroToModel()
    {
        if (_loading) return;

        int idx = Math.Clamp(_curMacro, 0, KbConfig.MacroMax - 1);
        var m = _model.Macros[idx];

        m.TimeoutMs = (ushort)_timeout.Value;
        m.Loop = _loop.Checked;

        // 步数 = 显式记的 _stepCount（不再用"连续勾选"表达，也不靠内容推断）
        int count = _stepCount;
        m.StepCount = (byte)count;

        for (int i = 0; i < count && i < StepRows; i++)
        {
            var r = _rows[i];
            var k = r.Key.SelectedItem as KeyOption ?? KeyCodes.None;
            m.Steps[i].Action    = (byte)r.Action.SelectedIndex;
            m.Steps[i].Keycode   = k.Code;
            m.Steps[i].Modifiers = k.Modifier;
            m.Steps[i].DelayMinMs = (ushort)r.Val1.Value;
            m.Steps[i].DelayMaxMs = (ushort)r.Val2.Value;
        }

        BindTrigger(_model, idx, _triggerBtn.SelectedIndex - 1);
    }

    /// <summary>
    /// 把宏 <paramref name="macroIdx"/> 的触发按钮设成 <paramref name="btnIdx"/>（-1 = 取消）。
    /// 两条约束都要满足：一个宏只由一个按钮触发；一个按钮只触发一个宏。
    /// </summary>
    private static void BindTrigger(KbConfig c, int macroIdx, int btnIdx)
    {
        for (int i = 0; i < KbConfig.SlotCount; i++)
        {
            if (c.Buttons[i].UsesMacro && c.Buttons[i].MacroId == macroIdx)
            {
                c.Buttons[i].UsesMacro = false;
                c.Buttons[i].MacroId = 0xFF;
            }
        }

        if (btnIdx < 0 || btnIdx >= KbConfig.SlotCount) return;

        c.Buttons[btnIdx].UsesMacro = true;
        c.Buttons[btnIdx].MacroId   = (byte)macroIdx;
    }

    // ══════════════════════════════════════════════════════════════
    //  外部接口
    // ══════════════════════════════════════════════════════════════

    public void UpdateLiveState(InputState st)
    {
        if (st.MacroBusy)
        {
            _liveState.Text = "设备：有宏正在执行";
            _liveState.ForeColor = DarkTheme.Ok;
            return;
        }

        // 空闲时不要覆盖掉"刚点了测试触发"的结果文字
        if (_liveState.Text.StartsWith("设备："))
        {
            _liveState.Text = "设备：空闲";
            _liveState.ForeColor = DarkTheme.TextDim;
        }
    }

    public void AttachDevice(bool connected)
    {
        if (!connected)
        {
            _liveState.Text = "设备未连接";
            _liveState.ForeColor = DarkTheme.TextDim;
        }
    }

    /// <summary>
    /// 在下拉里选中 (code, mod)。
    /// ★ 找不到时不能静默归零 —— 否则用户一点保存就把原值抹掉了
    ///   （和 KeymapPanel 里同样的处理，见那边的详细说明）。
    /// </summary>
    private static void SelectByKey(ComboBox cb, byte code, byte mod = 0)
    {
        if (code == 0 && mod == 0)
        {
            var none = KeyCodes.Find(0, 0);
            if (!cb.Items.Contains(none)) cb.Items.Insert(0, none);
            cb.SelectedItem = none;
            return;
        }

        var exact = KeyCodes.Find(code, mod);
        if (exact.Code == code && exact.Modifier == mod)
        {
            if (!cb.Items.Contains(exact)) cb.Items.Insert(0, exact);
            cb.SelectedItem = exact;
            return;
        }

        var byCode = KeyCodes.All.FirstOrDefault(k => k.Code == code && k.Modifier == 0)
                  ?? KeyCodes.All.FirstOrDefault(k => k.Code == code);

        var pick = byCode ?? new KeyOption($"未知(0x{code:X2}/mod 0x{mod:X2})", code, mod);
        if (!cb.Items.Contains(pick)) cb.Items.Insert(0, pick);
        cb.SelectedItem = pick;
    }
}
