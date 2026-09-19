namespace KbConfigurator.Ui;

/// <summary>
/// 自适应布局的小工具集。
///
/// ★ 为什么需要它：改造前三个面板里散着 <b>41 处</b>
///   <c>Location = new Point(16, 30)</c> 这样的绝对坐标。
///   后果是窗口一缩放控件就不跟随、开高 DPI 会错位，
///   而且每加一个控件都要手算坐标还得当心别和别的重叠。
///
///   改用 <see cref="TableLayoutPanel"/> / <see cref="FlowLayoutPanel"/> 之后，
///   位置由容器算，加控件只需要往下一行/下一列塞。
///   这里的几个工厂方法就是为了把样板代码压到最短 ——
///   不然每个面板都要写一遍 ColumnStyles/RowStyles 会很啰嗦，又容易漏。
/// </summary>
internal static class L
{
    /// <summary>竖向堆叠的表格：一列，每行按内容自动高</summary>
    public static TableLayoutPanel Stack()
    {
        var t = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 0,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = DarkTheme.PanelBack,
            Margin = new Padding(0),
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        return t;
    }

    /// <summary>竖向堆叠 + 自动滚动（内容比窗口高时用）</summary>
    public static Panel Scroll(Control content)
    {
        var p = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = DarkTheme.FormBack };
        content.Dock = DockStyle.Top;
        p.Controls.Add(content);
        return p;
    }

    /// <summary>往堆叠表格里加一行，行高按内容自适应</summary>
    public static void AddRow(TableLayoutPanel t, Control c, int marginTop = 0)
    {
        c.Margin = new Padding(0, marginTop, 0, 0);
        t.Controls.Add(c, 0, t.RowCount);
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowCount++;
    }

    /// <summary>让某一行的内容撑满剩余高度（每个堆叠表格最多用一次）</summary>
    public static void MakeGrow(TableLayoutPanel t, int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= t.RowStyles.Count) return;
        t.RowStyles[rowIndex] = new RowStyle(SizeType.Percent, 100f);
    }

    /// <summary>
    /// 建一个等宽的列布局表格。
    /// <paramref name="widths"/> 里的负数表示"按比例撑满"，正数表示固定像素。
    /// 例如 <c>Grid(-1, 120, -1)</c> = 第一列撑满、第二列 120px、第三列撑满。
    /// </summary>
    public static TableLayoutPanel Grid(params int[] widths)
    {
        var t = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = widths.Length,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = DarkTheme.PanelBack,
            Margin = new Padding(0),
        };

        float totalWeight = widths.Where(w => w < 0).Sum(w => -w);
        foreach (var w in widths)
        {
            t.ColumnStyles.Add(w < 0
                ? new ColumnStyle(SizeType.Percent, -w / totalWeight * 100f)
                : new ColumnStyle(SizeType.Absolute, w));
        }
        return t;
    }

    /// <summary>往 Grid 里加一个控件</summary>
    public static void Cell(TableLayoutPanel t, Control c, int col, int row, int padRight = 6)
    {
        c.Margin = new Padding(0, 2, padRight, 2);
        c.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        t.Controls.Add(c, col, row);
    }

    /// <summary>表格专用的行高</summary>
    public static void RowHeight(TableLayoutPanel t, int row, int h)
    {
        while (t.RowStyles.Count <= row) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles[row] = new RowStyle(SizeType.Absolute, h);
    }

    /// <summary>统一的分组框</summary>
    public static GroupBox Group(string title, Control content, int minHeight = 0)
    {
        var g = new GroupBox
        {
            Text = title,
            ForeColor = DarkTheme.TextDim,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 6, 10, 8),
            Margin = new Padding(0, 0, 0, 8),
        };
        if (minHeight > 0) g.MinimumSize = new Size(0, minHeight);
        g.Controls.Add(content);
        return g;
    }

    public static Label Text(string s, Color? color = null, bool bold = false, float size = 9f)
        => new()
        {
            Text = s,
            AutoSize = true,
            ForeColor = color ?? DarkTheme.Text,
            Font = DarkTheme.Ui(size, bold ? FontStyle.Bold : FontStyle.Regular),
            Margin = new Padding(0),
        };

    public static Label Header(string s, float size = 9f)
        => new()
        {
            Text = s,
            AutoSize = true,
            ForeColor = DarkTheme.TextDim,
            Font = DarkTheme.Ui(size),
            Margin = new Padding(0),
        };

    public static Label Title(string s)
        => new()
        {
            Text = s,
            AutoSize = true,
            ForeColor = DarkTheme.Text,
            Font = DarkTheme.Ui(12f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 4),
        };

    /// <summary>业务按钮（统一外观，可带强调色）</summary>
    public static Button Button(string text, int width = 110, Color? accent = null)
    {
        var b = new Button
        {
            Text = text,
            Width = width,
            Height = 28,
            FlatStyle = FlatStyle.Flat,
            BackColor = accent ?? DarkTheme.InputBack,
            ForeColor = DarkTheme.Text,
            Margin = new Padding(0, 0, 8, 0),
            UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderColor = DarkTheme.Border;
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(58, 62, 70);
        b.FlatAppearance.MouseDownBackColor = Color.FromArgb(70, 76, 86);
        return b;
    }

    /// <summary>小方按钮（表格里的 ↑ ↓ ✕ 之类）</summary>
    public static Button TinyButton(string text, string tip = "")
    {
        var b = new Button
        {
            Text = text,
            Width = 26,
            Height = 24,
            FlatStyle = FlatStyle.Flat,
            BackColor = DarkTheme.InputBack,
            ForeColor = DarkTheme.Text,
            Margin = new Padding(2, 0, 0, 0),
            UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderColor = DarkTheme.Border;
        if (tip.Length > 0) new ToolTip().SetToolTip(b, tip);
        return b;
    }

    /// <summary>只读数值框（不要把结果误改成可编辑）</summary>
    public static TextBox ReadOnlyBox(int width = 0)
    {
        var t = new TextBox
        {
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = DarkTheme.PanelBack,
            ForeColor = DarkTheme.Text,
            Font = DarkTheme.Mono(),
            TabStop = false,
        };
        if (width > 0) t.Width = width;
        else t.Dock = DockStyle.Fill;
        return t;
    }
}
