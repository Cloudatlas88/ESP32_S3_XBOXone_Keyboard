using System.Drawing.Drawing2D;

namespace KbConfigurator.Ui;

/// <summary>
/// 统一的暗色主题。
///
/// ★ 为什么需要它：以前每个面板各自写 <c>Color.FromArgb(32,34,38)</c> 之类的字面量，
///   三处面板 + 窗体各写一遍，改一次配色要翻四个文件，而且新加的控件经常忘了上色 ——
///   于是深色界面里冒出几个系统亮色控件，观感割裂。
///   现在全部走这里，新控件建好之后统一 <see cref="Apply"/> 一次即可。
///
/// ★ 折中方案（已确认的决策）：
///   ComboBox / NumericUpDown 这类**原生控件**只能改到"本体"的配色，
///   弹出的下拉列表由系统绘制，部分 Windows 版本上无法变暗。
///   要彻底暗色就得整套自绘控件（代码量陡增、还容易把输入体验做坏），
///   所以这里做到"控件本体暗色、下拉列表跟随系统"为止。
/// </summary>
internal static class DarkTheme
{
    // ── 配色 ──
    public static readonly Color FormBack   = Color.FromArgb(32, 34, 38);
    public static readonly Color PanelBack  = Color.FromArgb(38, 40, 45);
    public static readonly Color InputBack  = Color.FromArgb(46, 49, 55);
    public static readonly Color InputBackDisabled = Color.FromArgb(40, 42, 46);

    public static readonly Color Text       = Color.FromArgb(225, 228, 235);
    public static readonly Color TextDim    = Color.FromArgb(150, 156, 166);
    public static readonly Color TextDisabled = Color.FromArgb(110, 114, 122);

    public static readonly Color Accent     = Color.FromArgb(90, 150, 220);   // 主色（按钮/选中）
    public static readonly Color Ok         = Color.FromArgb(120, 200, 130);
    public static readonly Color Warn       = Color.FromArgb(210, 180, 90);
    public static readonly Color Error      = Color.FromArgb(230, 130, 90);
    public static readonly Color Border     = Color.FromArgb(58, 62, 70);

    // ── 字体 ──
    public const string FontFamily = "Microsoft YaHei UI";

    public static Font Ui(float size = 9f, FontStyle style = FontStyle.Regular)
        => new(FontFamily, size, style);

    /// <summary>等宽字体（数值/键码显示用，避免数字跳动）</summary>
    public static Font Mono(float size = 10f, FontStyle style = FontStyle.Bold)
        => new("Consolas", size, style);

    /// <summary>
    /// 安全地设成透明背景。
    ///
    /// ⚠️ **不是所有控件都支持透明背景**：`Control` 基类默认不支持，
    ///    直接赋 <c>Color.Transparent</c> 会抛
    ///    <c>ArgumentException("控件不支持透明的背景色")</c>。
    ///    `Panel`/`TableLayoutPanel`/`FlowLayoutPanel`/`Label` 支持；
    ///    自己继承 <c>Control</c> 写的自绘控件不支持 ——
    ///    那种情况要在构造函数里 <c>SetStyle(ControlStyles.SupportsTransparentBackColor, true)</c>。
    ///
    /// 这个坑是实打实崩过一次的（TravelBar 直接赋透明 → 程序启动即挂），
    /// 所以这里统一兜住，不要在各处直接写 `BackColor = Color.Transparent`。
    /// </summary>
    public static void SetTransparent(Control c)
    {
        try { c.BackColor = Color.Transparent; }
        catch (ArgumentException) { c.BackColor = PanelBack; }
    }

    /// <summary>
    /// 给一棵控件树统一上色。递归处理所有子控件。
    /// 在窗体构造末尾调用一次即可。
    /// </summary>
    public static void Apply(Control root)
    {
        root.BackColor = root is Form ? FormBack : PanelBack;
        root.ForeColor = Text;

        foreach (Control c in root.Controls)
        {
            Style(c);
            Apply(c);       // 递归
        }
    }

    private static void Style(Control c)
    {
        switch (c)
        {
            case TabControl tc:
                // TabControl 的页签头必须自绘才能变暗（原生页签头永远是系统色）
                tc.DrawMode = TabDrawMode.OwnerDrawFixed;
                tc.BackColor = FormBack;
                tc.ForeColor = Text;
                tc.ItemSize = new Size(Math.Max(96, tc.ItemSize.Width), 28);
                tc.SizeMode = TabSizeMode.Fixed;
                break;

            case TabPage:
                c.BackColor = FormBack;
                c.ForeColor = Text;
                break;

            case Button b:
                b.FlatStyle = FlatStyle.Flat;
                b.BackColor = InputBack;
                b.ForeColor = Text;
                b.FlatAppearance.BorderColor = Border;
                b.FlatAppearance.MouseOverBackColor = Color.FromArgb(58, 62, 70);
                b.FlatAppearance.MouseDownBackColor = Color.FromArgb(70, 76, 86);
                b.UseVisualStyleBackColor = false;
                break;

            case ComboBox cb:
                // ComboBox 必须设 FlatStyle=Flat 才能让 BackColor 生效
                // （默认的 Standard 样式下背景永远是系统白）
                cb.FlatStyle = FlatStyle.Flat;
                cb.BackColor = InputBack;
                cb.ForeColor = Text;
                cb.DrawMode = DrawMode.OwnerDrawFixed;   // 本体自绘，选中项文字才可见
                break;

            case NumericUpDown nu:
                nu.BackColor = InputBack;
                nu.ForeColor = Text;
                nu.BorderStyle = BorderStyle.FixedSingle;
                break;

            case TextBox tb:
                tb.BackColor = InputBack;
                tb.ForeColor = Text;
                tb.BorderStyle = BorderStyle.FixedSingle;
                break;

            case CheckBox chk:
                SetTransparent(chk);
                chk.ForeColor = Text;
                chk.FlatStyle = FlatStyle.Flat;
                chk.FlatAppearance.BorderColor = Border;
                chk.FlatAppearance.CheckedBackColor = Accent;
                break;

            case GroupBox g:
                g.ForeColor = TextDim;
                SetTransparent(g);
                break;

            case Label:
                SetTransparent(c);
                c.ForeColor = Text;
                break;

            case StatusStrip ss:
                ss.BackColor = Color.FromArgb(40, 42, 48);
                ss.ForeColor = TextDim;
                break;

            // ⚠️ 顺序要紧：TableLayoutPanel / FlowLayoutPanel 都继承自 Panel，
            //    所以必须排在 `case Panel` 前面，否则永远不会被匹配到。
            case TableLayoutPanel or FlowLayoutPanel:
                SetTransparent(c);
                break;

            case Panel p:
                p.BackColor = PanelBack;
                break;
        }
    }

    // ── TabControl 页签头自绘 ──

    public static void DrawTab(TabControl tc, DrawItemEventArgs e)
    {
        bool selected = (e.Index == tc.SelectedIndex);

        var back = selected ? FormBack : Color.FromArgb(42, 44, 50);
        using (var br = new SolidBrush(back))
            e.Graphics.FillRectangle(br, e.Bounds);

        // 选中页签用一条主色下划线强调，而不是靠底色深浅（暗色下不明显）
        if (selected)
        {
            using var pen = new Pen(Accent, 2f);
            e.Graphics.DrawLine(pen, e.Bounds.Left + 4, e.Bounds.Bottom - 1,
                                     e.Bounds.Right - 4, e.Bounds.Bottom - 1);
        }

        var text = tc.TabPages[e.Index].Text;
        using var font = Ui(9.5f, selected ? FontStyle.Bold : FontStyle.Regular);
        var color = selected ? Text : TextDim;
        using var tb = new SolidBrush(color);

        var sz = e.Graphics.MeasureString(text, font);
        e.Graphics.DrawString(text, font, tb,
            e.Bounds.Left + (e.Bounds.Width - sz.Width) / 2,
            e.Bounds.Top + (e.Bounds.Height - sz.Height) / 2);
    }

    /// <summary>把页签头自绘挂到 TabControl 上（构造时调用一次）</summary>
    public static void HookTabs(TabControl tc)
    {
        tc.DrawMode = TabDrawMode.OwnerDrawFixed;
        tc.DrawItem += (s, e) => DrawTab((TabControl)s!, e);
    }

    // ── ComboBox 本体自绘 ──

    /// <summary>
    /// 挂上 ComboBox 的本体自绘。必须挂，否则设了 BackColor 之后
    /// 选中项文字会看不见（系统按亮色前景画）。
    /// </summary>
    public static void HookCombo(ComboBox cb)
    {
        if (cb.DrawMode != DrawMode.OwnerDrawFixed) cb.DrawMode = DrawMode.OwnerDrawFixed;

        cb.DrawItem += (s, e) =>
        {
            var box = (ComboBox)s!;
            e.DrawBackground();

            bool selectedItem = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            var back = selectedItem ? Accent : (box.Enabled ? InputBack : InputBackDisabled);

            using (var br = new SolidBrush(back))
                e.Graphics.FillRectangle(br, e.Bounds);

            if (e.Index >= 0 && e.Index < box.Items.Count)
            {
                var text = box.Items[e.Index]?.ToString() ?? "";
                using var font = Ui(9f);
                using var tb = new SolidBrush(box.Enabled ? Text : TextDisabled);
                e.Graphics.DrawString(text, font, tb, e.Bounds.Left + 2, e.Bounds.Top + 1);
            }

            e.DrawFocusRectangle();
        };
    }

    /// <summary>
    /// TabControl 自绘页签头要按内容重画，所以窗口每次重绘都得挂钩。
    /// 在窗体构造末尾调用一次即可。
    /// </summary>
    public static void HookAllTabs(Form f)
    {
        foreach (var tc in Descendants(f).OfType<TabControl>())
            HookTabs(tc);

        foreach (var cb in Descendants(f).OfType<ComboBox>())
            HookCombo(cb);
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }
}
