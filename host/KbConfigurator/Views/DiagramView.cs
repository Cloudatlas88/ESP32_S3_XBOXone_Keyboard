using KbConfigurator.Model;
using KbConfigurator.Protocol;
using KbConfigurator.Ui;

namespace KbConfigurator.Views;

/// <summary>
/// 手柄示意图 + 热区层（自绘控件）。
///
/// ★ 为什么自绘而不是往窗体上摆 PictureBox + 一堆 Panel：
///   热区位置全部是**归一化坐标**，必须随窗口缩放按比例重算；
///   用子控件的话每次 Resize 都要重设 15 个控件的 Location/Size，
///   而且窗口很小的时候热区会互相重叠、被容器裁掉。
///   自绘只需要一份映射公式，任何尺寸都对，也不会有子控件重叠问题。
///
/// 本步骤（设计文档 §七 步骤 1）只做"画出来"：
///   · 图片等比缩放居中
///   · 每个热区画一个圈/徽章 + 名称
///   · 鼠标悬停高亮（用来证明命中判定是准的）
/// 实时读数高亮是步骤 2，可编辑映射是步骤 3。
/// </summary>
public sealed class DiagramView : Control
{
    /// <summary>单键热区半径（屏幕像素，固定 —— 不随缩放变，保证任何尺寸都点得到）</summary>
    public const int RadiusSingle = 17;

    /// <summary>方向类热区半径（大一点，因为它代表 4 个映射）</summary>
    public const int RadiusDir4 = 25;

    /// <summary>徽章半宽/半高（屏幕像素，固定尺寸）</summary>
    public const int BadgeHalfW = 34, BadgeHalfH = 15;

    /// <summary>徽章圆角半径</summary>
    public const int BadgeCorner = 7;

    /// <summary>命中判定的宽容半径：没直接点在圈里时，附近这么多像素内也算</summary>
    public const int HitSlack = 16;

    // ══════════════════════════════════════════════════════════════════
    //  图层专用配色
    //
    //  ★ 不能直接用 DarkTheme 的那套：那套是给**深色窗口**配的
    //    （正文色 (225,228,235) 是浅灰）。而手柄示意图是**浅色**的
    //    （背景接近 #F0F0F0，轮廓和文字是黑的）。把浅灰文字画上去
    //    等于隐形 —— 第一版就是这么错的，截图上"名字"和徽章里的
    //    "L3/R3"一个字都看不见，只剩几个空圈。
    //
    //  所以这里单独一套：在浅色图上对比度足够，且避开图里已经用掉的
    //  颜色（引线是蓝的、轮廓和文字是黑的）。
    // ══════════════════════════════════════════════════════════════════

    /// <summary>常态圈色：橙色，和图的蓝引线、黑轮廓都不撞</summary>
    private static readonly Color RingIdle = Color.FromArgb(214, 116, 16);

    /// <summary>悬停圈色：红，明确区分"光标在这"</summary>
    private static readonly Color RingHover = Color.FromArgb(198, 40, 40);

    /// <summary>暂缓项（LT/RT）：灰</summary>
    private static readonly Color RingOff = Color.FromArgb(140, 140, 146);

    /// <summary>★ 实时高亮：绿。和常态橙、悬停红都拉开距离，一眼能分清</summary>
    private static readonly Color RingActive = Color.FromArgb(24, 158, 68);

    /// <summary>高亮时的填充（比圈色浅，避免盖掉底下的按键图形）</summary>
    private static readonly Color ActiveFill = Color.FromArgb(96, 60, 200, 110);

    /// <summary>未激活方向箭头</summary>
    private static readonly Color ArrowIdle = Color.FromArgb(70, 110, 112, 120);

    /// <summary>
    /// 映射牌底色。
    /// ★ 必须**接近示意图的背景色**：这层牌子是贴上去盖住图上原有的英文描述文字的，
    ///   颜色差一点就会看到一块明显的补丁。原图背景是接近 #F0F0F0 的平灰。
    /// </summary>
    private static readonly Color PlateFill = Color.FromArgb(240, 240, 240);
    private static readonly Color PlateEdge = Color.FromArgb(196, 198, 202);

    /// <summary>映射牌里"待升级"这类灰字的颜色</summary>
    private static readonly Color PlateDim = Color.FromArgb(150, 152, 158);

    /// <summary>徽章（L3/R3）：紫，和按键圈区分开</summary>
    private static readonly Color BadgeEdge = Color.FromArgb(138, 58, 186);
    private static readonly Color BadgeFill = Color.FromArgb(238, 226, 248);

    /// <summary>图上的说明文字必须**深色**，因为底是浅色图</summary>
    private static readonly Color OverlayText = Color.FromArgb(24, 24, 28);

    /// <summary>文字底衬：半透明白，保证压在深色轮廓上也读得出来</summary>
    private static readonly Color TextPlate = Color.FromArgb(210, 255, 255, 255);

    private XboxLayout? _layout;
    private Image? _image;
    private string _loadError = "";

    private int _hoverIndex = -1;
    private readonly List<LayoutItem> _drawOrder = new();

    /// <summary>当前实时高亮状态（未接设备时为 null）</summary>
    private SlotMap.Highlights? _hl;

    /// <summary>槽位号 → 当前映射的显示文字（"B" / "Ctrl+A" / "—"）。未读取配置时为 null</summary>
    private string[]? _slotText;

    /// <summary>槽位号 → 该槽位在当前固件下能不能编辑</summary>
    private bool[]? _slotEditable;

    /// <summary>点在某个热区上（item, 客户区坐标）—— 用来弹映射编辑面板</summary>
    public event Action<LayoutItem, Point>? HotspotActivated;

    /// <summary>校准模式下坐标被拖动（每次移动都触发，调用方据此标记"有未保存的改动"）</summary>
    public event Action? LayoutEdited;

    // ── 校准模式 ──
    private bool _calib;
    private LayoutItem? _dragItem;
    private int _dragKind;                 // 0 无 / 1 拖坐标点 / 2 拖标签框
    private float _grabDx, _grabDy;        // 抓取点相对目标原点的偏移（屏幕像素）
    private LayoutItem? _calibHover;       // 校准模式下光标所在的项

    /* ══════════════════════════════════════════════════════════════
     *  ★★ 静态层缓存 —— 解决"手柄一动 UI 就卡"的问题
     *
     *  原来每帧（20Hz）的 OnPaint 都要做两件极贵的事：
     *    1. 把 2400×1350 的示意图用**双三次插值**缩放到窗口尺寸
     *    2. 重画 15 块映射牌，每块都跑一遍 FitFont ——
     *       而 FitFont 从 9pt 往下试，最坏要造 7 个 Font 再丢弃，
     *       也就是每帧上百次 GDI 字体分配
     *
     *  这两样加起来远超一帧的预算，消息队列越堆越多，
     *  表现就是"推一下摇杆，图上的圈要过好久才动，而且越拖越久"。
     *
     *  但这两样**都不随输入变化**：图是固定的，映射牌只在配置/尺寸变了才变。
     *  所以把它们画进一张缓存位图，每帧只做一次贴图 + 十几次小圆绘制。
     *
     *  缓存失效条件：窗口尺寸变了 / 槽位文字变了 / 校准拖动 / 图层开关变了。
     * ══════════════════════════════════════════════════════════════ */
    private Bitmap? _staticLayer;
    private int _staticVersion;      // 内容版本号，任何静态内容变化就 ++
    private int _builtVersion = -1;
    private Size _builtSize;

    /// <summary>声明静态层内容已过期（尺寸以外的变化都要调它）</summary>
    private void InvalidateStaticLayer()
    {
        _staticVersion++;
        Invalidate();
    }

    /// <summary>
    /// 测试用：强制丢弃静态层，让下一次绘制走"全量重画"的慢路径。
    /// 有了它才能**相对地**验证缓存有效 —— 绝对耗时随机器快慢变，
    /// 但"复用缓存 vs 每次重建"的比值是稳定的。
    /// </summary>
    internal void ForceStaticLayerRebuildForTest() => InvalidateStaticLayer();

    /// <summary>
    /// 测试用：OnPaint 真正被执行的次数。
    /// ★ 为什么要这个：`Control.Paint` 事件在**未显示的**控件上不会触发，
    ///   而 `DrawToBitmap` 内部走 WM_PRINT，也不走那条路径 ——
    ///   第一版用 Paint 事件计数，结果"50 次调用 0 次绘制"是空跑通过的假绿。
    ///   直接在 OnPaint 里数，才是"到底重画了没有"的直接证据。
    /// </summary>
    internal int PaintCountForTest { get; private set; }

    /// <summary>
    /// 测试用：SetHighlights 累计**请求重绘**的次数。
    ///
    /// ★ 为什么不数 OnPaint：未显示的控件不产生 WM_PAINT，屏幕外的窗体也不产生。
    ///   三条路都试过（Refresh / Update / DrawToBitmap）——
    ///   其中 DrawToBitmap 最坑：它**强制**渲染，不管有没有请求重绘都会画，
    ///   于是"没变就不重绘"必然测出 51 次，测到的是渲染次数而不是重绘请求。
    ///   而这个优化要保证的契约恰恰就是"内容没变就不请求重绘"，所以数请求才对。
    /// </summary>
    internal int RepaintRequestCountForTest { get; private set; }

    /// <summary>坐标点手柄的边长</summary>
    public const int DotHandleSize = 22;

    /// <summary>校准模式下标签框的抓取范围（太细的话点不中）</summary>
    public const int LabelGrabPad = 4;

    /// <summary>
    /// 布局校准模式：热区变成可拖拽的方框（见设计文档 §四）。
    ///
    /// ★ 为什么需要它：坐标是程序从图上量出来的（tools/gen_xbox_layout.py），
    ///   但"控件放在这里好不好看"只有人能看到。有了这个模式，
    ///   换图或微调坐标不用改代码、不用重编译。
    /// </summary>
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    [System.ComponentModel.Browsable(false)]
    public bool CalibrationMode
    {
        get => _calib;
        set
        {
            if (_calib == value) return;
            _calib = value;
            _dragItem = null;
            _dragKind = 0;
            Cursor = Cursors.Default;
            InvalidateStaticLayer();   // 校准牌画在静态层里
        }
    }

    /// <summary>正在拖动某一项（校准模式下只允许拖一个）</summary>
    public LayoutItem? DraggingItem => _dragKind != 0 ? _dragItem : null;

    /// <summary>坐标点的屏幕手柄矩形</summary>
    public RectangleF DotHandleOf(LayoutItem it)
    {
        var c = CenterOf(it);
        if (c.IsEmpty) return RectangleF.Empty;
        return new RectangleF(c.X - DotHandleSize / 2f, c.Y - DotHandleSize / 2f,
                              DotHandleSize, DotHandleSize);
    }

    /// <summary>标签框的屏幕矩形（含抓取余量）</summary>
    public RectangleF LabelGrabOf(LayoutItem it)
    {
        var r = LabelRectOf(it);
        return r.IsEmpty ? RectangleF.Empty : RectangleF.Inflate(r, LabelGrabPad, LabelGrabPad);
    }

    /// <summary>校准模式下命中测试：优先坐标点手柄，其次标签框</summary>
    public (LayoutItem? Item, int Kind) CalibHit(Point p)
    {
        if (_layout is null) return (null, 0);

        // 坐标点手柄小，优先判它，否则会被标签框抢走
        foreach (var it in _drawOrder)
            if (DotHandleOf(it).Contains(p)) return (it, 1);

        foreach (var it in _drawOrder)
            if (LabelGrabOf(it).Contains(p)) return (it, 2);

        return (null, 0);
    }

    /// <summary>是否把每个热区当前的映射画在图上的原文字位置（"描述 → 映射"）</summary>
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    [System.ComponentModel.Browsable(false)]
    public bool ShowMappingPlates
    {
        get => _showPlates;
        set
        {
            if (_showPlates == value) return;
            _showPlates = value;
            InvalidateStaticLayer();   // 映射牌画在静态层里
        }
    }
    private bool _showPlates = true;

    /// <summary>设置槽位显示文字（由面板从配置里算好传入）</summary>
    public void SetSlotText(string[]? text, bool[]? editable)
    {
        _slotText = text;
        _slotEditable = editable;
        InvalidateStaticLayer();   // 映射牌的文字变了，静态层要重画
    }

    /// <summary>某个槽位当前是否可编辑</summary>
    public bool SlotEditable(int slot)
        => _slotEditable is not null && slot >= 0 && slot < _slotEditable.Length && _slotEditable[slot];

    /// <summary>
    /// 设置实时高亮状态。
    /// ★ 这里只存引用不复制：Input Report 是 20Hz，每帧复制一遍
    ///   HashSet 是没必要的开销；调用方每帧填同一个对象即可。
    ///
    /// ★★ 而且**内容没变就不重绘**。
    ///   摇杆居中、没按键的时候，20Hz 的重绘纯属白干 —— 界面上一个像素都不会变。
    ///   用一份位签名（24 位槽位 + 3 组方向 + 两个标志）比对，变了才 Invalidate。
    ///   "最小延迟"和"不滥用资源"其实是同一件事：少做无用的重绘，
    ///   CPU 才有余量在真正变化的那一刻立刻响应。
    /// </summary>
    public void SetHighlights(SlotMap.Highlights? hl)
    {
        _hl = hl;

        ulong sig = SignatureOf(hl);
        if (sig == _lastHighlightSig) return;      // 一模一样，不重绘
        _lastHighlightSig = sig;

        RepaintRequestCountForTest++;
        Invalidate();
    }

    private ulong _lastHighlightSig = ulong.MaxValue;

    /// <summary>
    /// 高亮状态的精确位签名（是位图不是哈希，不会有碰撞）。
    /// ulong 有 64 位：24 个槽位 + 12 位方向 + 2 个标志，够用。
    /// </summary>
    private static ulong SignatureOf(SlotMap.Highlights? hl)
    {
        if (hl is null) return 0;
        ulong sig = hl.Valid ? (1UL << 63) : 0UL;
        if (hl.MacroBusy) sig |= 1UL << 62;
        // ★ 上界必须用 SlotMap.SlotCount，不能写死。
        //   这里原来写的是 `< 24`，扩到 26 槽位后槽位 24/25（LT/RT）
        //   就进不了签名 —— 只按扳机时签名不变，SetHighlights 判定"没变化"
        //   直接 return，一次重绘都不发，界面上扳机永远不亮。
        //   （更阴的是：同时按了别的键它又会亮，属于"有时候好有时候不好"。）
        foreach (var s in hl.Active)
            if (s >= 0 && s < SlotMap.SlotCount) sig |= 1UL << s;
        sig |= (ulong)(hl.DirsOf("dpad")   & 0xF) << 24;
        sig |= (ulong)(hl.DirsOf("lstick") & 0xF) << 28;
        sig |= (ulong)(hl.DirsOf("rstick") & 0xF) << 32;
        return sig;
    }

    /// <summary>某一项当前是否处于激活状态（单键看槽位，方向类看有没有方向）</summary>
    public bool IsActive(LayoutItem it)
    {
        if (_hl is null || !_hl.Valid) return false;
        if (it.IsDir4) return _hl.DirsOf(it.Id) != 0;
        return it.Slots.Any(_hl.IsSlotActive);
    }

    /// <summary>
    /// 是否把**所有**热区的名字都画出来。
    /// 默认 false —— 只在悬停时显示光标下那个的名字，界面上才不至于糊成一片。
    /// 打开它用于截图/核对（<c>--shot</c> 会打开），此时每项都带名字。
    /// </summary>
    /// <remarks>
    /// 加 DesignerSerializationVisibility 是因为 WinForms 分析器（WFO1000）要求
    /// 自绘控件的可写属性明确声明序列化策略；这个控件只在代码里 new，不进设计器。
    /// </remarks>
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    [System.ComponentModel.Browsable(false)]
    public bool ShowAllNames { get; set; }

    public DiagramView()
    {
        // UserPaint + AllPaintingInWmPaint + OptimizedDoubleBuffer：
        // 自己画全部内容，并且双缓冲 —— 否则每次悬停重绘都会闪
        SetStyle(ControlStyles.UserPaint
               | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.ResizeRedraw, true);
        BackColor = DarkTheme.FormBack;
        TabStop = false;
    }

    /// <summary>当前加载的布局定义（未加载时为 null）</summary>
    public XboxLayout? LayoutData => _layout;
    public Image? DiagramImage => _image;
    public string LoadError => _loadError;

    /// <summary>当前屏幕映射（每次按当前尺寸现算，不依赖是否已经绘制过）</summary>
    public DiagramMap Map => _layout is null
        ? default
        : new DiagramMap(_layout.ImageW, _layout.ImageH, ClientSize.Width, ClientSize.Height);

    /// <summary>按绘制顺序排列的热区（所有项，含暂缓项）</summary>
    public IReadOnlyList<LayoutItem> DrawOrder => _drawOrder;

    public void Load(XboxLayout layout)
    {
        _layout = layout;
        _drawOrder.Clear();
        _drawOrder.AddRange(layout.All);

        _image?.Dispose();
        _image = null;
        _loadError = "";

        var path = layout.ResolveImagePath();
        if (!File.Exists(path))
        {
            // 不抛异常 —— 缺图片时页面仍要能用（画占位提示），
            // 否则资源没拷到输出目录就直接崩了，用户只看到"程序打不开"
            _loadError = $"找不到示意图：{path}";
        }
        else
        {
            try
            {
                // 从文件流读并复制一份，避免 Image 一直占着文件句柄
                using var fs = File.OpenRead(path);
                using var raw = Image.FromStream(fs);
                _image = new Bitmap(raw);
            }
            catch (Exception ex)
            {
                _loadError = $"示意图读取失败：{ex.Message}";
            }
        }

        InvalidateStaticLayer();
    }

    /// <summary>热区中心在屏幕上的位置</summary>
    public PointF CenterOf(LayoutItem it)
    {
        var m = Map;
        return it.Dot.Length == 2 ? m.ToScreen(it.Dot[0], it.Dot[1]) : PointF.Empty;
    }

    public static int RadiusOf(LayoutItem it) => it.Kind switch
    {
        "dir4" => RadiusDir4,
        "badge" => BadgeHalfW,
        _ => RadiusSingle,
    };

    /// <summary>
    /// 命中判定。先精确命中，再在 <see cref="HitSlack"/> 内找最近的 ——
    /// 徽章和方向圈比较大，但小圈之间还是有缝，给一点宽容更好点。
    /// </summary>
    public LayoutItem? HitTest(Point p)
    {
        int idx = IndexAt(p);
        return idx >= 0 ? _drawOrder[idx] : null;
    }

    public int IndexAt(Point p)
    {
        if (_layout is null || !Map.IsValid) return -1;

        int best = -1;
        float bestD = float.MaxValue;

        for (int i = 0; i < _drawOrder.Count; i++)
        {
            var it = _drawOrder[i];
            var c = CenterOf(it);
            float dx = p.X - c.X, dy = p.Y - c.Y;
            float d = MathF.Sqrt(dx * dx + dy * dy);

            if (it.IsBadge)
            {
                // 徽章是矩形，按矩形判（圆形判定会让四个角点不到）
                if (MathF.Abs(dx) <= BadgeHalfW && MathF.Abs(dy) <= BadgeHalfH) return i;
            }
            else if (d <= RadiusOf(it))
            {
                return i;
            }

            float reach = RadiusOf(it) + HitSlack;
            if (d <= reach && d < bestD) { bestD = d; best = i; }
        }

        return best;
    }

    /// <summary>
    /// 强制把某一项画成悬停态（按 id；找不到就清除）。
    /// 给截图模式用 —— 截图要能看出"悬停长什么样"，否则每次都得让用户自己去晃鼠标。
    /// </summary>
    internal void SetHoverById(string? id)
    {
        _hoverIndex = -1;
        if (id is not null)
            for (int i = 0; i < _drawOrder.Count; i++)
                if (_drawOrder[i].Id == id) { _hoverIndex = i; break; }
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_calib)
        {
            if (_dragKind != 0 && _dragItem is not null) DoDrag(e.Location);
            else
            {
                var (it, kind) = CalibHit(e.Location);
                Cursor = it is null ? Cursors.Default
                       : kind == 1 ? Cursors.SizeAll : Cursors.Hand;

                // 悬停项变了要重绘：坐标只在悬停/拖动的那一项上显示（见 DrawCalibrationOverlay）
                if (!ReferenceEquals(it, _calibHover))
                {
                    _calibHover = it;
                    Invalidate();
                }
            }
            return;
        }

        int idx = IndexAt(e.Location);
        if (idx == _hoverIndex) return;

        _hoverIndex = idx;
        Cursor = idx >= 0 ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!_calib || e.Button != MouseButtons.Left) return;

        var (it, kind) = CalibHit(e.Location);
        if (it is null) return;

        _dragItem = it;
        _dragKind = kind;

        if (kind == 1)
        {
            var c = CenterOf(it);
            _grabDx = e.X - c.X;
            _grabDy = e.Y - c.Y;
        }
        else
        {
            var r = LabelRectOf(it);
            _grabDx = e.X - r.Left;
            _grabDy = e.Y - r.Top;
        }

        Invalidate();
    }

    /// <summary>拖动中：把鼠标位置换算成新坐标</summary>
    private void DoDrag(Point p)
    {
        var it = _dragItem!;
        var m = Map;
        if (!m.IsValid || _layout is null) return;

        float tx = p.X - _grabDx;
        float ty = p.Y - _grabDy;

        if (_dragKind == 1)
        {
            // 拖坐标点：直接把归一化坐标设成鼠标换算值，并夹在 0..1
            var n = m.ToNormalized(tx + DotHandleSize / 2f, ty + DotHandleSize / 2f);
            it.Dot[0] = Math.Clamp(n.X, 0, 1);
            it.Dot[1] = Math.Clamp(n.Y, 0, 1);
            // 像素坐标同步一下，方便排查时对照
            it.DotPx = new[] { (int)Math.Round(it.Dot[0] * _layout.ImageW),
                               (int)Math.Round(it.Dot[1] * _layout.ImageH) };
        }
        else if (_dragKind == 2 && it.LabelPx.Length == 4)
        {
            // 拖标签框：算屏幕位移，再换算成原图像素位移
            var r = LabelRectOf(it);
            float dxImg = (tx - r.Left) / m.DrawW * _layout.ImageW;
            float dyImg = (ty - r.Top) / m.DrawH * _layout.ImageH;

            it.LabelPx = new[]
            {
                it.LabelPx[0] + (int)Math.Round(dxImg),
                it.LabelPx[1] + (int)Math.Round(dyImg),
                it.LabelPx[2] + (int)Math.Round(dxImg),
                it.LabelPx[3] + (int)Math.Round(dyImg),
            };
        }

        InvalidateStaticLayer();   // 拖标签框会移动映射牌
        LayoutEdited?.Invoke();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (_calib)
        {
            if (_dragKind != 0) { _dragKind = 0; _dragItem = null; Invalidate(); }
            return;
        }

        if (e.Button != MouseButtons.Left) return;

        var it = HitTest(e.Location);
        if (it is not null) HotspotActivated?.Invoke(it, e.Location);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverIndex < 0) return;
        _hoverIndex = -1;
        Cursor = Cursors.Default;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        PaintCountForTest++;
        var g = e.Graphics;
        g.Clear(DarkTheme.FormBack);

        if (_layout is null)
        {
            DrawCentered(g, "尚未加载布局");
            return;
        }

        var m = Map;
        if (!m.IsValid)
        {
            if (_loadError.Length > 0) DrawCentered(g, _loadError);
            return;
        }

        // ── 静态层：图 + 映射牌（变化时才重画）──
        EnsureStaticLayer(m);
        if (_staticLayer is not null)
            g.DrawImageUnscaled(_staticLayer, 0, 0);

        // ── 动态层：高亮圈 / 悬停 / 方向箭头（每帧都要跟输入变）──
        for (int i = 0; i < _drawOrder.Count; i++)
            DrawHotspot(g, _drawOrder[i], i == _hoverIndex);

        // 悬停的那一块映射牌要重画一遍：静态层里存的是**非悬停态**，
        // 否则鼠标移上去牌子不会有任何反应。只画 1 块，开销可以忽略。
        if (ShowMappingPlates && _hoverIndex >= 0 && _hoverIndex < _drawOrder.Count)
            DrawMappingPlate(g, _drawOrder[_hoverIndex], hover: true);

        // 校准层画在最上面：它要盖住一切，免得被映射牌挡着抓不到
        if (_calib) DrawCalibrationOverlay(g);

        if (_loadError.Length > 0)
        {
            using var f = new Font(MonoFam, 8f);
            using var b = new SolidBrush(DarkTheme.Error);
            g.DrawString(_loadError, f, b, 8, 6);
        }
    }

    /// <summary>静态层过期时重建（画图 + 映射牌）</summary>
    private void EnsureStaticLayer(DiagramMap m)
    {
        if (_staticLayer is not null
            && _builtVersion == _staticVersion
            && _builtSize == ClientSize
            && _staticLayer.Size == ClientSize)
            return;

        _staticLayer?.Dispose();
        _staticLayer = null;
        _builtVersion = _staticVersion;
        _builtSize = ClientSize;

        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;

        var bmp = new Bitmap(ClientSize.Width, ClientSize.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(DarkTheme.FormBack);

            if (_image is not null)
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                var r = m.ImageRect;
                g.DrawImage(_image, r.X, r.Y, r.Width, r.Height);
            }
            else
            {
                using var ph = new SolidBrush(DarkTheme.PanelBack);
                g.FillRectangle(ph, m.ImageRect);
                using var pb = new Pen(DarkTheme.Border);
                g.DrawRectangle(pb, m.ImageRect.X, m.ImageRect.Y,
                                m.ImageRect.Width, m.ImageRect.Height);
            }

            // 映射牌：盖住图上原有的英文描述文字，也盖住压在那一带的高亮圈
            if (ShowMappingPlates)
                for (int i = 0; i < _drawOrder.Count; i++)
                    DrawMappingPlate(g, _drawOrder[i], hover: false);
        }

        _staticLayer = bmp;
    }

    private void DrawHotspot(Graphics g, LayoutItem it, bool hover)
    {
        var c = CenterOf(it);
        if (c.IsEmpty) return;

        bool active = IsActive(it);

        // 颜色优先级：暂缓(灰) > 实时激活(绿) > 悬停(红) > 常态(橙)
        Color edge = it.Kind switch
        {
            "deferred" => RingOff,
            "badge" => active ? RingActive : BadgeEdge,
            _ => active ? RingActive : (hover ? RingHover : RingIdle),
        };

        if (it.IsBadge)
        {
            var rect = new RectangleF(c.X - BadgeHalfW, c.Y - BadgeHalfH,
                                      BadgeHalfW * 2, BadgeHalfH * 2);

            // 圆角矩形：GDI+ 没有现成的，得自己拼路径
            using var path = RoundedRect(rect, BadgeCorner);

            // 徽章用**实心浅色**而不是半透明：半透明压在图上会变成
            // 说不清的颜色，字也跟着糊；实心底才能保证字读得出来
            Color bg = active ? Color.FromArgb(206, 240, 214)
                     : hover ? Color.FromArgb(222, 204, 242)
                             : BadgeFill;
            using (var fill = new SolidBrush(bg))
                g.FillPath(fill, path);
            using (var pen = new Pen(edge, active || hover ? 3.2f : 2.2f))
                g.DrawPath(pen, path);

            using var f = new Font(MonoFam, 9.5f, FontStyle.Bold);
            var txt = it.Id.ToUpperInvariant();
            var sz = g.MeasureString(txt, f);
            using var tb = new SolidBrush(OverlayText);      // ★ 深色，不是主题的浅灰
            g.DrawString(txt, f, tb, c.X - sz.Width / 2, c.Y - sz.Height / 2);
            return;
        }

        int r = RadiusOf(it);

        // 常态**不填充**：图上本来就密，填色会盖掉按键本身。
        // 悬停给一层很淡的反馈；实时激活给一层明显的绿。
        if (active)
        {
            using var fill = new SolidBrush(ActiveFill);
            g.FillEllipse(fill, c.X - r, c.Y - r, r * 2, r * 2);
        }
        else if (hover)
        {
            using var fill = new SolidBrush(Color.FromArgb(46, edge));
            g.FillEllipse(fill, c.X - r, c.Y - r, r * 2, r * 2);
        }

        using (var pen = new Pen(edge, active ? 4.0f : hover ? 3.5f : 2.6f))
            g.DrawEllipse(pen, c.X - r, c.Y - r, r * 2, r * 2);

        // 方向类：圈里画十字（暗示是 4 个映射），圈外画 4 个方向箭头，
        // 推到哪个方向哪个箭头就亮 —— 这是 §3.3 要求的"箭头实时跟着动"
        if (it.IsDir4)
        {
            using (var cross = new Pen(Color.FromArgb(170, edge), 1.6f))
            {
                g.DrawLine(cross, c.X - r * 0.42f, c.Y, c.X + r * 0.42f, c.Y);
                g.DrawLine(cross, c.X, c.Y - r * 0.42f, c.X, c.Y + r * 0.42f);
            }

            int bits = _hl?.DirsOf(it.Id) ?? 0;
            DrawDirArrow(g, c.X, c.Y - r - 4, 0, (bits & SlotMap.DirUp) != 0);
            DrawDirArrow(g, c.X, c.Y + r + 4, 2, (bits & SlotMap.DirDown) != 0);
            DrawDirArrow(g, c.X - r - 4, c.Y, 3, (bits & SlotMap.DirLeft) != 0);
            DrawDirArrow(g, c.X + r + 4, c.Y, 1, (bits & SlotMap.DirRight) != 0);
        }

        // 名字只在悬停时显示（或者 ShowAllNames 打开时全显示）。
        // 全显示会糊成一片 —— ABXY 几个圈本来就挨得近。
        if (!hover && !ShowAllNames) return;
        DrawChip(g, it.Name, c.X, c.Y + r + 10,
                 it.Kind == "deferred" ? RingOff : OverlayText);
    }

    /// <summary>
    /// 画一个方向箭头。dir: 0=上 1=右 2=下 3=左。
    /// 未激活时画得很淡（保留位置提示），激活时实心绿。
    /// </summary>
    private static void DrawDirArrow(Graphics g, float x, float y, int dir, bool on)
    {
        const float L = 9f;      // 从顶点到底边的长度
        const float W = 6f;      // 半宽

        PointF[] tri = dir switch
        {
            0 => new[] { new PointF(x, y - L), new PointF(x - W, y), new PointF(x + W, y) },
            2 => new[] { new PointF(x, y + L), new PointF(x - W, y), new PointF(x + W, y) },
            3 => new[] { new PointF(x - L, y), new PointF(x, y - W), new PointF(x, y + W) },
            _ => new[] { new PointF(x + L, y), new PointF(x, y - W), new PointF(x, y + W) },
        };

        if (on)
        {
            using var b = new SolidBrush(RingActive);
            g.FillPolygon(b, tri);
        }
        else
        {
            using var p = new Pen(ArrowIdle, 1.6f);
            g.DrawPolygon(p, tri);
        }
    }

    /// <summary>
    /// 带底衬的小字标签。
    /// ★ 底衬是必须的：这些字压在图上，背后可能是白的背景、也可能是黑的轮廓，
    ///   没有底衬就总有一半位置读不出来。
    /// </summary>
    private static void DrawChip(Graphics g, string text, float cx, float topY, Color fg)
    {
        using var f = new Font(MonoFam, 7.5f);
        var sz = g.MeasureString(text, f);
        var pad = 3f;
        var rect = new RectangleF(cx - sz.Width / 2 - pad, topY - 1,
                                  sz.Width + pad * 2, sz.Height + 1);

        using (var plate = new SolidBrush(TextPlate))
            g.FillRectangle(plate, rect);
        using (var b = new SolidBrush(fg))
            g.DrawString(text, f, b, rect.X + pad, rect.Y);
    }

    /// <summary>
    /// 等宽字体族（懒加载一次）。
    /// ★ 不要每次绘制都写 <c>DarkTheme.Mono().FontFamily</c> —— DarkTheme.Mono()
    ///   每次都 new 一个 Font，字体会一直不释放（GDI 句柄泄漏），
    ///   而这个控件在悬停时要按 20Hz 重绘。
    /// </summary>
    private static FontFamily? _monoFam;
    private static FontFamily MonoFam => _monoFam ??= DarkTheme.Mono().FontFamily;

    /// <summary>图上原文字标签框 → 屏幕矩形（归一化后按当前缩放算）</summary>
    public RectangleF LabelRectOf(LayoutItem it)
    {
        if (_layout is null || it.LabelPx.Length != 4) return RectangleF.Empty;
        var m = Map;
        if (!m.IsValid) return RectangleF.Empty;

        var a = m.ToScreen(it.LabelPx[0] / (double)_layout.ImageW,
                           it.LabelPx[1] / (double)_layout.ImageH);
        var b = m.ToScreen(it.LabelPx[2] / (double)_layout.ImageW,
                           it.LabelPx[3] / (double)_layout.ImageH);
        return RectangleF.FromLTRB(a.X, a.Y, b.X, b.Y);
    }

    /// <summary>某个热区当前映射的可读文字（方向类拼成 "↑=W ↓=S ←=A →=D"）</summary>
    public string MappingTextOf(LayoutItem it)
    {
        if (_slotText is null) return "";
        if (!it.IsDir4)
            return it.Slots.Count > 0 && it.Slots[0] < _slotText.Length ? _slotText[it.Slots[0]] : "";

        var arrow = new[] { "↑", "↓", "←", "→" };
        var parts = new List<string>();
        for (int i = 0; i < it.Slots.Count && i < arrow.Length; i++)
        {
            int s = it.Slots[i];
            if (s < 0 || s >= _slotText.Length) continue;
            // 用 "=" 而不是把箭头直接拼在键名前面：方向键的键名本身就带箭头
            // （"↑ 上 (Up)"），拼起来会变成 "↑↑" 完全看不懂
            parts.Add($"{arrow[i]}={_slotText[s]}");
        }
        return string.Join(" ", parts);
    }

    /// <summary>
    /// 方向类热区在映射牌上该怎么写。
    /// ★ 如果 4 个槽位**都**还不能编辑（旧固件下的十字键就是），
    ///   不要拼 4 遍"待升级" —— 那串字又长又挤，最后被压成 6pt 谁也看不清，
    ///   反而比一句"待升级"信息量更低。
    /// </summary>
    public string PlateTextOf(LayoutItem it)
    {
        if (it.Slots.Count > 1 && it.Slots.All(s => !SlotEditable(s)))
            return $"{it.Name}  待升级";

        var tail = MappingTextOf(it);
        return tail.Length > 0 ? $"{it.Name}  {tail}" : it.Name;
    }

    /// <summary>这个热区里有没有任何一个槽位在当前固件下可编辑</summary>
    public bool AnySlotEditable(LayoutItem it) => it.Slots.Any(SlotEditable);

    /// <summary>
    /// 画映射牌：把图上原有的英文描述文字**盖掉**，换成"名称 + 当前映射"。
    ///
    /// ★ 牌子必须用接近原图背景的颜色不透明覆盖：原图上的英文是烧进 PNG 里的，
    ///   擦不掉，只能盖。用半透明会透出底下的字，变成两层叠字。
    ///
    /// ★ 牌子宽度**按文字需要撑开**，不必等于原文字框：
    ///   原文字框是"英文描述"的宽度，而我们要写的是中文名 + 4 个方向映射
    ///   （"左摇杆 ↑=↑ ↓=↓ ←=← →=→"），比原文字长得多。
    ///   死守原框会把字压成 6pt 谁也看不清 —— 那还不如不写。
    ///   撑开后往两侧都可能在留白里，不会盖住手柄本体。
    /// </summary>
    private void DrawMappingPlate(Graphics g, LayoutItem it, bool hover)
    {
        var r = LabelRectOf(it);
        bool slotless = r.IsEmpty;
        if (slotless)
        {
            // 没有原文字框的项（L3/R3 徽章）：在徽章下方贴一块牌子
            var c = CenterOf(it);
            if (c.IsEmpty) return;
            r = new RectangleF(c.X - BadgeHalfW, c.Y + BadgeHalfH + 4, BadgeHalfW * 2, 19);
        }

        string text = PlateTextOf(it);

        // 先按目标字号量一下需要多大，再决定牌子尺寸
        const float TargetSize = 8.5f;
        using var probe = new Font(MonoFam, TargetSize);
        var need = g.MeasureString(text, probe);

        float w = Math.Max(r.Width, need.Width + 8);
        float h = Math.Max(r.Height, need.Height + 3);

        // 缩得太小时牌子会糊成一片 —— 干脆不画。
        // 热区圈还在，信息不算丢；这也让最小窗口尺寸下的布局验收能过。
        if (r.Width < 30 || r.Height < 9) return;

        w = Math.Min(w, ClientSize.Width - 8);
        h = Math.Min(h, Math.Max(10, ClientSize.Height - 8));

        // 以原文字框中心为锚点撑开，然后夹进控件范围
        float cx = r.Left + r.Width / 2f;
        float cy = r.Top + r.Height / 2f;
        float x = Math.Clamp(cx - w / 2f, 4, Math.Max(4, ClientSize.Width - w - 4));
        float y = Math.Clamp(cy - h / 2f, 4, Math.Max(4, ClientSize.Height - h - 4));

        var plate = new RectangleF(x, y, w, h);

        using (var b = new SolidBrush(PlateFill)) g.FillRectangle(b, plate);
        using (var p = new Pen(hover ? RingHover : PlateEdge, hover ? 1.8f : 1f))
            g.DrawRectangle(p, plate.X, plate.Y, plate.Width, plate.Height);

        bool editable = AnySlotEditable(it);
        using var f = FitFont(g, text, plate.Width - 6, plate.Height - 1);
        using var tb = new SolidBrush(editable ? OverlayText : PlateDim);

        // ★ 用带省略号的矩形绘制，而不是 DrawString(x, y)：
        //   万一还是放不下（窗口极小），直接画出去会溢出牌子、盖住旁边的图。
        //   给矩形 + EllipsisCharacter，放不下就自动截断成 "…"。
        using var fmt = new StringFormat(StringFormatFlags.NoWrap)
        {
            Trimming = StringTrimming.EllipsisCharacter,
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Center,
        };
        var textRect = new RectangleF(plate.X + 3, plate.Y + 1,
                                      Math.Max(1, plate.Width - 6),
                                      Math.Max(1, plate.Height - 2));
        g.DrawString(text, f, tb, textRect, fmt);
    }

    /// <summary>
    /// 校准层：每一项画一个可拖拽的**坐标点手柄**和一个可拖拽的**标签框**，
    /// 并标出名字与当前归一化坐标（拖的时候要能看着数字对齐）。
    ///
    /// ★ 画在最上层：映射牌会盖住标签框所在的位置，
    ///   如果校准层在下面，用户会抓不到标签框。
    /// </summary>
    private void DrawCalibrationOverlay(Graphics g)
    {
        using var dotPen = new Pen(Color.FromArgb(0, 110, 205), 1.6f);
        using var lblPen = new Pen(Color.FromArgb(0, 155, 90), 1.4f)
        {
            DashStyle = System.Drawing.Drawing2D.DashStyle.Dash,
        };
        using var grabPen = new Pen(RingHover, 2.8f);
        using var plateBrush = new SolidBrush(Color.FromArgb(218, 255, 255, 255));
        using var textBrush = new SolidBrush(OverlayText);
        using var f = new Font(MonoFam, 7.5f);

        foreach (var it in _drawOrder)
        {
            bool draggingDot = _dragKind == 1 && ReferenceEquals(it, _dragItem);
            bool draggingLbl = _dragKind == 2 && ReferenceEquals(it, _dragItem);

            // ── 标签框（拖动它 = 移动映射牌的位置）──
            var lr = LabelRectOf(it);
            if (!lr.IsEmpty)
            {
                using (var b = new SolidBrush(Color.FromArgb(28, 0, 155, 90)))
                    g.FillRectangle(b, lr);
                g.DrawRectangle(draggingLbl ? grabPen : lblPen,
                                lr.X, lr.Y, lr.Width, lr.Height);
            }

            // ── 坐标点手柄（拖动它 = 移动热区中心）──
            var dh = DotHandleOf(it);
            if (dh.IsEmpty) continue;

            using (var b = new SolidBrush(Color.FromArgb(56, 0, 110, 205)))
                g.FillRectangle(b, dh);
            g.DrawRectangle(draggingDot ? grabPen : dotPen, dh.X, dh.Y, dh.Width, dh.Height);

            // ★ 坐标数字只画在**悬停或正在拖**的那一项上。
            //   全画出来的话，ABXY 那一片十几个数字会糊成一团谁也不看清，
            //   而校准的时候人只需要看自己正在动的那个。
            bool detail = draggingDot || draggingLbl || ReferenceEquals(it, _calibHover);
            string txt = detail ? $"{it.Name}  {it.Dot[0]:F4},{it.Dot[1]:F4}" : it.Name;

            using var f2 = new Font(MonoFam, detail ? 8f : 7f);
            var sz = g.MeasureString(txt, f2);
            var tr = new RectangleF(dh.Right + 4, dh.Top - 2, sz.Width + 6, sz.Height + 2);
            g.FillRectangle(plateBrush, tr);
            g.DrawString(txt, f2, textBrush, tr.X + 3, tr.Y + 1);
        }
    }

    /// <summary>从大到小挑一个能塞进给定宽高的字号（最小 6pt）</summary>
    private static Font FitFont(Graphics g, string text, float maxW, float maxH)
    {
        for (float size = 9f; size > 6f; size -= 0.5f)
        {
            var f = new Font(MonoFam, size);
            var sz = g.MeasureString(text, f);
            if (sz.Width <= maxW && sz.Height <= maxH) return f;
            f.Dispose();          // 不合适就立刻释放，别攒着
        }
        return new Font(MonoFam, 6f);
    }

    /// <summary>
    /// 测试用：模拟一次完整的拖动（按下 → 移动 → 松开）。
    /// 比在测试里写反射调 OnMouseXxx 清楚得多，也更容易看出测的是什么。
    /// </summary>
    internal void SimulateDrag(Point from, Point to)
    {
        OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, from.X, from.Y, 0));
        OnMouseMove(new MouseEventArgs(MouseButtons.Left, 0, to.X, to.Y, 0));
        OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, to.X, to.Y, 0));
    }

    /// <summary>测试用：模拟一次点击</summary>
    internal void SimulateClick(Point p, MouseButtons btn = MouseButtons.Left)
    {
        OnMouseDown(new MouseEventArgs(btn, 1, p.X, p.Y, 0));
        OnMouseUp(new MouseEventArgs(btn, 1, p.X, p.Y, 0));
    }

    /// <summary>拼一个圆角矩形路径（GDI+ 没有内置的）</summary>
    internal static System.Drawing.Drawing2D.GraphicsPath RoundedRect(RectangleF r, float rad)
    {
        var p = new System.Drawing.Drawing2D.GraphicsPath();
        float d = rad * 2;
        if (d > r.Width) d = r.Width;
        if (d > r.Height) d = r.Height;

        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private void DrawCentered(Graphics g, string msg)
    {
        using var f = new Font(MonoFam, 10f);
        using var b = new SolidBrush(DarkTheme.TextDim);
        var sz = g.MeasureString(msg, f);
        g.DrawString(msg, f, b,
                     (ClientSize.Width - sz.Width) / 2, (ClientSize.Height - sz.Height) / 2);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _image?.Dispose();
        base.Dispose(disposing);
    }
}
