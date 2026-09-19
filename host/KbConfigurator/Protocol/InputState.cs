namespace KbConfigurator.Protocol;

/// <summary>
/// 设备通过厂商接口 Input Report (RID 0x04) 推送的输入状态帧。
///
/// ⚠️ 字节布局必须与固件严格一致（**52 字节**，全小端）。
///    固件那边的唯一事实来源是 <c>app_config.h</c> 的 <c>IN_OFF_*</c>，
///    每段边界都有 _Static_assert 钉着。契约文档：项目结构.md §六
///
///     偏移  字段              长度  说明
///     0     flags             1     bit0 数据有效 / bit1 USB已挂载 / bit2 运行期贴轨
///     1-4   slots             4     ★ 26 个槽位，bit0 = 槽位 0 …… bit25 = 槽位 25
///     5     dirs_dpad         1     十字键：bit0 上 / bit1 下 / bit2 左 / bit3 右
///     6     dirs_lstick       1     左摇杆：同上
///     7     dirs_rstick       1     右摇杆：同上
///     8     stat              1     bit1 = 有宏正在执行
///     9-16  raw[4]            8     ★ 四轴原始值：左X 左Y 右X 右Y（各 uint16）
///     17-24 center[4]         8     四轴中心（开机自校准）
///     25-28 uptime_ms         4
///     29-32 seq               4
///     33    axis_fault        1     bit0..3 = 四轴故障（悬空/贴轨，已禁用）
///     34-37 btn_raw[4]        4     ★ 26 个槽位的**原始 GPIO 电平**（未防抖，接线诊断用）
///     38-53 travel[4]        16     ★ 四轴行程极值：x,y 各 min/max 后接 rx,ry
///
/// ★ 0x0004 相比 0x0003 的变化：
///   · 按键位 1 字节 → **3 字节**（5 个按钮装不下 24 个槽位）
///   · 方向从 1 组 → **3 组**（十字键 / 左摇杆 / 右摇杆，旧格式只有一组会互相串）
///   · 新增右摇杆的原始值、中心、行程
///   · btn_raw 1 字节 → 4 字节（它是"边接边验"最有用的字段）
///   · 0x0005：槽位 24 → 26（追加 LT/RT），于是 slots 与 btn_raw 各 3 → 4 字节
///   · 不再单独传百分比 —— 由上位机用 raw/center/travel 现算（省 4 字节，
///     而且保证显示值和方向判定用的是同一套数）
/// </summary>
public sealed class InputState
{
    public const int PayloadSize = 54;

    /// <summary>四轴编号（与固件 AXIS_* 一致）</summary>
    public const int AxisLX = 0, AxisLY = 1, AxisRX = 2, AxisRY = 3;
    public const int AxisCount = 4;

    public byte Flags { get; init; }

    /// <summary>★ 24 个槽位的按下位图（bit0 = 槽位 0）</summary>
    public uint Slots { get; init; }

    public byte DirsDpad   { get; init; }
    public byte DirsLstick { get; init; }
    public byte DirsRstick { get; init; }
    public byte Stat       { get; init; }

    /// <summary>四轴原始值 0..4095</summary>
    public ushort[] Raw    { get; init; } = new ushort[AxisCount];

    /// <summary>四轴中心（开机自校准）</summary>
    public ushort[] Center { get; init; } = new ushort[AxisCount];

    /// <summary>四轴归一化 -100..+100（用 raw/center/行程现算，见 <see cref="Percent"/>）</summary>
    public sbyte[] Pct     { get; init; } = new sbyte[AxisCount];

    /// <summary>四轴行程自学习极值</summary>
    public ushort[] TMin   { get; init; } = new ushort[AxisCount];
    public ushort[] TMax   { get; init; } = new ushort[AxisCount];

    public uint UptimeMs { get; init; }
    public uint Seq      { get; init; }

    /// <summary>bit0..3 = LX/LY/RX/RY 故障（悬空或贴轨，该轴已禁用）</summary>
    public byte AxisFault { get; init; }

    /// <summary>★ 24 个槽位的原始 GPIO 电平（未防抖）—— 接线诊断用</summary>
    public byte[] BtnRaw { get; init; } = new byte[4];

    /// <summary>当前有宏正在执行（循环宏开启后会一直是 true，直到再按一次停止）</summary>
    public bool MacroBusy => (Stat & 0x02) != 0;

    // ── flags 位 ──
    public bool Valid       => (Flags & 0x01) != 0;
    public bool UsbMounted  => (Flags & 0x02) != 0;
    public bool RuntimeRail => (Flags & 0x04) != 0;

    public bool FaultOf(int axis) => axis >= 0 && axis < AxisCount && (AxisFault & (1 << axis)) != 0;

    /// <summary>某轴行程跨度</summary>
    public int SpanOf(int axis) => TMax[axis] - TMin[axis];

    /// <summary>行程是否已收敛（跨度明显小于满量程 4095 说明学到了真实行程）</summary>
    public bool TravelLearnedOf(int axis) => SpanOf(axis) < 3900;

    /// <summary>某个槽位当前是否按下</summary>
    public bool Slot(int i) => i >= 0 && i < 32 && (Slots & (1u << i)) != 0;

    /// <summary>
    /// 某个槽位的原始电平（未防抖）是否读到低电平（= 按下）。
    /// ★ 上界用"位图容量"（BtnRaw.Length*8）而不是槽位数 ——
    ///   这样以后再扩槽位，这里不用跟着改（写死 24 就是这么漏掉 LT/RT 的）。
    /// </summary>
    public bool RawLevel(int slot)
        => slot >= 0 && slot < 32 && (BtnRaw[slot / 8] & (1 << (slot % 8))) != 0;

    // ── 方向位图 ──
    public static bool DirUpOf(byte d)    => (d & 0x01) != 0;
    public static bool DirDownOf(byte d)  => (d & 0x02) != 0;
    public static bool DirLeftOf(byte d)  => (d & 0x04) != 0;
    public static bool DirRightOf(byte d) => (d & 0x08) != 0;

    /// <summary>把方向位图写成"上左下右"这样的可读文字</summary>
    public static string DirText(byte d)
    {
        if (d == 0) return "—";
        var s = "";
        if (DirUpOf(d)) s += "上";
        if (DirDownOf(d)) s += "下";
        if (DirLeftOf(d)) s += "左";
        if (DirRightOf(d)) s += "右";
        return s;
    }

    /// <summary>从原始负载解析。长度按段递进读，短帧只丢后面几段而不整体失败。</summary>
    public static InputState Parse(ReadOnlySpan<byte> p)
    {
        if (p.Length < 34) throw new ArgumentException($"输入帧太短：{p.Length} 字节（需要 ≥34）");

        var raw = new ushort[AxisCount];
        var ctr = new ushort[AxisCount];
        var tmn = new ushort[AxisCount];
        var tmx = new ushort[AxisCount];
        var pct = new sbyte[AxisCount];

        for (int a = 0; a < AxisCount; a++)
        {
            int ro = 9 + a * 2;
            int co = 17 + a * 2;
            int to = 38 + a * 4;

            raw[a] = (ushort)(p[ro] | (p[ro + 1] << 8));
            ctr[a] = (ushort)(p[co] | (p[co + 1] << 8));

            if (to + 3 < p.Length)
            {
                tmn[a] = (ushort)(p[to] | (p[to + 1] << 8));
                tmx[a] = (ushort)(p[to + 2] | (p[to + 3] << 8));
            }
            else { tmn[a] = 0; tmx[a] = 4095; }

            pct[a] = Percent(raw[a], ctr[a], tmn[a], tmx[a]);
        }

        return new InputState
        {
            Flags      = p[0],
            Slots      = (uint)(p[1] | (p[2] << 8) | (p[3] << 16) | (p[4] << 24)),
            DirsDpad   = p[5],
            DirsLstick = p[6],
            DirsRstick = p[7],
            Stat       = p[8],
            Raw        = raw,
            Center     = ctr,
            TMin       = tmn,
            TMax       = tmx,
            Pct        = pct,
            UptimeMs   = (uint)(p[25] | (p[26] << 8) | (p[27] << 16) | (p[28] << 24)),
            Seq        = (uint)(p[29] | (p[30] << 8) | (p[31] << 16) | (p[32] << 24)),
            AxisFault  = p[33],
            BtnRaw     = p.Length > 37 ? new[] { p[34], p[35], p[36], p[37] } : new byte[4],
        };
    }

    /// <summary>
    /// 归一化到 -100..+100。
    /// ★ **算法必须与固件 input_scan.c 的 normalize() 逐行一致**，
    ///   否则界面显示的百分比和固件判方向用的数会对不上 ——
    ///   表现就是"界面显示推到 60% 但方向已经出来了"这类说不清的差异。
    /// </summary>
    public static sbyte Percent(ushort raw, ushort center, ushort tmin, ushort tmax)
    {
        const int Deadband = 6;                 // 与固件 STICK_DEADBAND_RAW 一致
        int d = raw - center;
        if (d > -Deadband && d < Deadband) return 0;

        int p;
        if (d >= 0)
        {
            int span = tmax - center;
            p = span > 1 ? d * 100 / span : 100;
        }
        else
        {
            int span = center - tmin;
            p = span > 1 ? d * 100 / span : -100;
        }

        if (p >  100) p =  100;
        if (p < -100) p = -100;
        return (sbyte)p;
    }

    /// <summary>轴故障的可读文字（没有故障返回空串）</summary>
    public string FaultText()
    {
        var list = new List<string>();
        string[] names = { "左摇杆X", "左摇杆Y", "右摇杆X", "右摇杆Y" };
        for (int a = 0; a < AxisCount; a++)
            if (FaultOf(a)) list.Add(names[a] + "已禁用");
        if (RuntimeRail) list.Add("运行期贴轨");
        return list.Count == 0 ? "" : "⚠ " + string.Join(" / ", list);
    }
}
