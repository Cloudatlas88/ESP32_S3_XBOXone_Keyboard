namespace KbConfigurator.Ui;

/// <summary>
/// 把示意图的**归一化坐标**映射到控件的**屏幕像素**，以及反向映射。
///
/// ★ 为什么单独抽出来：这是整个按键布局页的核心数学，也是最容易写错的地方 ——
///   信箱式（letterbox）缩放少减一次偏移、宽高比取错一个分支，
///   表现就是"图看着对、热区整体偏了一截"，在窗口缩放时才暴露。
///   抽成不依赖 Control 的只读结构之后，可以脱离窗口直接断言往返一致性。
///
/// 规则：等比缩放到**完整放进**控件，居中，四周留黑边（不拉伸、不裁切）。
/// </summary>
public readonly struct DiagramMap
{
    public readonly int ImageW, ImageH, CtrlW, CtrlH;
    public readonly float Scale;      // 图片像素 -> 屏幕像素
    public readonly float DrawW, DrawH;
    public readonly float OffX, OffY;

    public DiagramMap(int imageW, int imageH, int ctrlW, int ctrlH)
    {
        ImageW = imageW; ImageH = imageH; CtrlW = ctrlW; CtrlH = ctrlH;

        if (imageW <= 0 || imageH <= 0 || ctrlW <= 0 || ctrlH <= 0)
        {
            Scale = DrawW = DrawH = OffX = OffY = 0f;
            return;
        }

        // 取较小的那个比例 —— 这就是"完整放进"而不是"填满"
        Scale = MathF.Min(ctrlW / (float)imageW, ctrlH / (float)imageH);
        DrawW = imageW * Scale;
        DrawH = imageH * Scale;
        OffX = (ctrlW - DrawW) / 2f;
        OffY = (ctrlH - DrawH) / 2f;
    }

    /// <summary>映射是否可用（控件尺寸为 0 时会不可用，要提前判断避免除零）</summary>
    public bool IsValid => Scale > 0f && DrawW > 0f && DrawH > 0f;

    /// <summary>归一化坐标 (0~1) -> 屏幕坐标</summary>
    public PointF ToScreen(double nx, double ny)
        => new(OffX + (float)(nx * DrawW), OffY + (float)(ny * DrawH));

    /// <summary>屏幕坐标 -> 归一化坐标（校准模式拖动时要用）</summary>
    public PointF ToNormalized(float sx, float sy)
        => IsValid ? new PointF((sx - OffX) / DrawW, (sy - OffY) / DrawH) : PointF.Empty;

    /// <summary>图片在控件里的绘制矩形</summary>
    public RectangleF ImageRect => new(OffX, OffY, DrawW, DrawH);

    /// <summary>归一化坐标是否落在图片范围内（留一点余量给热区半径）</summary>
    public static bool InRange(double nx, double ny, double pad = 0)
        => nx >= -pad && nx <= 1 + pad && ny >= -pad && ny <= 1 + pad;
}
