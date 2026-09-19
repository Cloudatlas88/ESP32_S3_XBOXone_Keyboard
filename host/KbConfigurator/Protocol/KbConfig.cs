namespace KbConfigurator.Protocol;

/// <summary>
/// 设备配置 —— 必须与固件 kb_config.h 的 <c>kb_config_t</c> 逐字节一致（396 字节）。
///
/// 版本历史：
///   0x0001  初始（164 字节）
///   0x0002  加宏定义区（396 字节）
///   0x0003  随机延迟改为【基准值 + 抖动%】；新增宏总开关按键与上电默认状态 ★当前
///
/// 字节布局：
///   0-1   magic      0x4B42
///   2-3   version    0x0003
///   4-5   size       396
///   6-7   crc16      覆盖【偏移 8 之后】的 388 字节
///   8-15  stick
///   16-55 buttons[5] × 8 (keycode, modifiers, trigger, flags, macroId, rsv[3])
///   56    macro_count
///   57-59 reserved[3]   ← 原「宏总开关按键 / 上电默认启用」，功能已移除
///   60-331 macros[4] × 68 (stepCount, flags, timeout, steps[8] × 8)
///   332-395 reserved[64]
///
/// ⚠️ 结构体 CRC 的起点是偏移 8（4 个 uint16 之后），不是 sizeof(crc16)=2。
/// </summary>
public sealed class KbConfig
{
    public const ushort Magic   = 0x4B42;
    public const ushort Version = 0x0006;   // ★ 宏步数 8 → 20
    public const int    Size    = 956;
    public const int    CrcOffset = 8;

    /// <summary>槽位数量（= 可映射项数量）。★ 与固件 SLOT_COUNT、layout.json 的 slots 一致</summary>
    public const int SlotCount = 26;

    /// <summary>摇杆数量（左、右各一套死区/反向）</summary>
    public const int StickCount = 2;

    public const int    MacroMax    = 4;
    public const int    MacroStepMax = 20;

    /// <summary>随机延迟抖动的上限（%）—— 再大分布就退化了</summary>
    public const int MaxJitterPct = 90;

    /// <summary>宏 flags：循环执行（触发按钮变成开/关切换）</summary>
    public const byte MacroFlagLoop = 0x01;

    // 按钮 flags
    public const byte BtnFlagEnabled  = 0x01;
    public const byte BtnFlagHasMacro = 0x02;

    // 按钮触发方式
    public const byte TrigPress   = 0;
    public const byte TrigRelease = 1;
    public const byte TrigHold    = 2;

    // 宏动作
    public const byte ActKeyTap     = 0;
    public const byte ActCombo      = 1;
    public const byte ActKeyDown    = 2;
    public const byte ActKeyUp      = 3;
    public const byte ActDelay      = 4;
    public const byte ActRandDelay  = 5;

    public static readonly string[] ActionNames =
        { "单键敲击", "组合键", "按住", "释放", "固定延迟", "随机延迟" };

    // ── 摇杆 ──
    /// <summary>
    /// 单个摇杆的**轴向行为**。
    ///
    /// ★ 0x0004 起这里**只放轴向行为，不放键映射**。
    ///   0x0003 时摇杆的四方向键码存在这里（UpKey/DownKey/...），
    ///   而 0x0004 把 24 个槽位的键映射**全部收进 Buttons[]**（含两个摇杆的 8 个方向）。
    ///   为什么改：两处都能写同一份映射，改一处另一处还是旧值，
    ///   而且"哪个生效"要靠读代码才知道。单一来源省掉整类 bug。
    /// </summary>
    public sealed class Stick
    {
        public byte DeadzonePct { get; set; } = 12;   // 0 ~ 60
        public byte InvertX     { get; set; }
        public byte InvertY     { get; set; }

        public Stick Clone() => (Stick)MemberwiseClone();
    }

    /// <summary>[0] = 左摇杆（ADC GPIO1/2） [1] = 右摇杆（ADC GPIO4/5）</summary>
    public Stick[] Sticks { get; } =
        Enumerable.Range(0, StickCount).Select(_ => new Stick()).ToArray();

    // ── 按钮 ──
    public sealed class Btn
    {
        public byte Keycode   { get; set; }
        public byte Modifiers { get; set; }
        public byte Trigger   { get; set; }
        public byte Flags     { get; set; }
        public byte MacroId   { get; set; } = 0xFF;

        public bool Enabled
        {
            get => (Flags & BtnFlagEnabled) != 0;
            set => Flags = (byte)(value ? Flags | BtnFlagEnabled : Flags & ~BtnFlagEnabled);
        }

        public bool UsesMacro
        {
            get => (Flags & BtnFlagHasMacro) != 0;
            set => Flags = (byte)(value ? Flags | BtnFlagHasMacro : Flags & ~BtnFlagHasMacro);
        }
    }

    /// <summary>
    /// 24 个槽位的映射，下标即槽位号（与 layout.json 的 slots、固件 slot_idx_t 一致）。
    ///
    /// ★ 槽位 16..23（两个摇杆的 8 个方向）也在这里 ——
    ///   固件从 ADC 判出方向后到对应槽位取键码发出去。
    ///   不要再另开一套"方向键配置"（见 <see cref="Stick"/> 的说明）。
    /// </summary>
    public Btn[] Buttons { get; } = Enumerable.Range(0, SlotCount).Select(_ => new Btn()).ToArray();

    // ── 宏 ──
    public sealed class Step
    {
        public byte Action    { get; set; }
        public byte Keycode   { get; set; }
        public byte Modifiers { get; set; }

        /// <summary>固定延迟 = 毫秒数；随机延迟 = 【基准值】毫秒</summary>
        public ushort DelayMinMs { get; set; }

        /// <summary>仅随机延迟用：【抖动百分比】0~90。0 = 退化成固定延迟</summary>
        public ushort DelayMaxMs { get; set; }

        public Step Clone() => (Step)MemberwiseClone();

        public override string ToString() => Action switch
        {
            ActKeyTap or ActCombo or ActKeyDown or ActKeyUp
                => $"{ActionNames[Action]} 键码=0x{Keycode:X2}" + (Modifiers != 0 ? $" mod=0x{Modifiers:X2}" : ""),
            ActRandDelay => DelayMaxMs == 0
                ? $"随机延迟 基准 {DelayMinMs}ms（无抖动）"
                : $"随机延迟 基准 {DelayMinMs}ms ±{DelayMaxMs}%",
            _ => $"{ActionNames[Action]} {DelayMinMs}ms",
        };
    }

    public sealed class Macro
    {
        public byte StepCount { get; set; }
        public byte Flags     { get; set; }        // bit0 = 循环
        public ushort TimeoutMs { get; set; }      // 0 = 用固件默认 30000
        public Step[] Steps { get; } = Enumerable.Range(0, MacroStepMax).Select(_ => new Step()).ToArray();

        public bool Loop
        {
            get => (Flags & 1) != 0;
            set => Flags = (byte)(value ? Flags | 1 : Flags & ~1);
        }

        public Macro Clone()
        {
            var m = new Macro { StepCount = StepCount, Flags = Flags, TimeoutMs = TimeoutMs };
            for (int i = 0; i < MacroStepMax; i++) m.Steps[i] = Steps[i].Clone();
            return m;
        }
    }

    public byte MacroCount { get; set; }
    public Macro[] Macros { get; } = Enumerable.Range(0, MacroMax).Select(_ => new Macro()).ToArray();

    // ── 出厂默认（与固件 kb_config_default() 一致）──
    public static KbConfig CreateDefault()
    {
        var c = new KbConfig { MacroCount = 0 };

        // 摇杆轴向行为：两个摇杆各自独立
        for (int s = 0; s < StickCount; s++)
        {
            c.Sticks[s].DeadzonePct = 12;
            c.Sticks[s].InvertX = 0;
            c.Sticks[s].InvertY = 0;
        }

        // ★ 必须与固件 kb_config_default() 的 defkey[] 逐项一致，
        //   否则"恢复默认"在固件和上位机两边会给出不同结果。
        //   两个摇杆刻意用不同的键（WASD / IJKL）—— 都用方向键会互相覆盖。
        byte[] def =
        {
            0x04, 0x05, 0x1B, 0x1C,      //  0- 3  A  B  X  Y
            0x14, 0x08,                  //  4- 5  LB RB      -> Q E
            0x2B, 0x29, 0x00, 0x00,      //  6- 9  View Menu Xbox Share -> Tab Esc — —
            0x00, 0x00,                  // 10-11  L3 R3     -> 不映射
            0x52, 0x51, 0x50, 0x4F,      // 12-15  十字键 ↑ ↓ ← →
            0x1A, 0x16, 0x04, 0x07,      // 16-19  左摇杆 ↑ ↓ ← →  -> W S A D
            0x17, 0x0D, 0x0C, 0x0E,      // 20-23  右摇杆 ↑ ↓ ← →  -> I J K L
            0x00, 0x00,                  // 24-25  LT RT     -> 不映射（由用户自己定）
        };

        for (int i = 0; i < SlotCount; i++)
        {
            c.Buttons[i].Keycode = def[i];
            c.Buttons[i].Trigger = TrigPress;
            // 没有默认键的槽位标成"未启用"，免得它悄悄发一个 0x00 出去
            c.Buttons[i].Enabled = def[i] != 0;
            c.Buttons[i].MacroId = 0xFF;
        }
        return c;
    }

    // ── 序列化 ──

    public byte[] ToBytes()
    {
        var b = new byte[Size];
        WriteU16(b, 0, Magic);
        WriteU16(b, 2, Version);
        WriteU16(b, 4, Size);
        WriteU16(b, 6, 0);

        // ── stick[2] × 8 字节（★ 只放轴向行为）──
        for (int s = 0; s < StickCount; s++)
        {
            int o = 8 + s * 8;
            b[o + 0] = Sticks[s].DeadzonePct;
            b[o + 1] = Sticks[s].InvertX;
            b[o + 2] = Sticks[s].InvertY;
            // o+3..o+7 保留（0x0003 时这里是 up/down/left/right 四个键码）
        }

        // ── buttons[24] × 8 字节 ──
        int btnBase = 8 + StickCount * 8;      // 24
        for (int i = 0; i < SlotCount; i++)
        {
            int o = btnBase + i * 8;
            b[o + 0] = Buttons[i].Keycode;
            b[o + 1] = Buttons[i].Modifiers;
            b[o + 2] = Buttons[i].Trigger;
            b[o + 3] = Buttons[i].Flags;
            b[o + 4] = Buttons[i].MacroId;
        }

        int off = btnBase + SlotCount * 8;      // 216
        b[off + 0] = MacroCount;
        // off+1 / off+2 是保留字节（原宏总开关/上电默认，功能已移除），留 0
        off += 4;

        for (int m = 0; m < MacroMax; m++)
        {
            WriteU16(b, off + 2, Macros[m].TimeoutMs);
            b[off + 0] = Macros[m].StepCount;
            b[off + 1] = Macros[m].Flags;
            off += 4;
            for (int s = 0; s < MacroStepMax; s++)
            {
                var st = Macros[m].Steps[s];
                b[off + 0] = st.Action;
                b[off + 1] = st.Keycode;
                b[off + 2] = st.Modifiers;
                b[off + 3] = 0;
                WriteU16(b, off + 4, st.DelayMinMs);
                WriteU16(b, off + 6, st.DelayMaxMs);
                off += 8;
            }
        }

        WriteU16(b, 6, Crc16.Compute(b.AsSpan(CrcOffset)));
        return b;
    }

    public static KbConfig FromBytes(ReadOnlySpan<byte> b)
    {
        if (b.Length < Size) throw new ArgumentException($"配置长度 {b.Length} < {Size}");

        var c = new KbConfig();

        // ── stick[2] × 8 字节 ──
        for (int s = 0; s < StickCount; s++)
        {
            int o = 8 + s * 8;
            c.Sticks[s].DeadzonePct = b[o + 0];
            c.Sticks[s].InvertX     = b[o + 1];
            c.Sticks[s].InvertY     = b[o + 2];
        }

        // ── buttons[24] × 8 字节 ──
        int btnBase = 8 + StickCount * 8;      // 24
        for (int i = 0; i < SlotCount; i++)
        {
            int o = btnBase + i * 8;
            c.Buttons[i].Keycode   = b[o + 0];
            c.Buttons[i].Modifiers = b[o + 1];
            c.Buttons[i].Trigger   = b[o + 2];
            c.Buttons[i].Flags     = b[o + 3];
            c.Buttons[i].MacroId   = b[o + 4];
        }

        int off = btnBase + SlotCount * 8;     // 216
        c.MacroCount     = b[off + 0];
        // b[off+1] / b[off+2] 是保留字节，不再读取（老配置里存着旧值也无所谓）
        off += 4;

        for (int m = 0; m < MacroMax; m++)
        {
            c.Macros[m].StepCount = b[off + 0];
            c.Macros[m].Flags     = b[off + 1];
            c.Macros[m].TimeoutMs = ReadU16(b, off + 2);
            off += 4;
            for (int s = 0; s < MacroStepMax; s++)
            {
                c.Macros[m].Steps[s].Action     = b[off + 0];
                c.Macros[m].Steps[s].Keycode    = b[off + 1];
                c.Macros[m].Steps[s].Modifiers  = b[off + 2];
                c.Macros[m].Steps[s].DelayMinMs = ReadU16(b, off + 4);
                c.Macros[m].Steps[s].DelayMaxMs = ReadU16(b, off + 6);
                off += 8;
            }
        }
        return c;
    }

    /// <summary>校验 magic / version / size / 结构体 CRC</summary>
    public static (bool Ok, string Reason) Validate(ReadOnlySpan<byte> b)
    {
        if (b.Length != Size) return (false, $"长度 {b.Length} != {Size}");

        ushort magic = ReadU16(b, 0), ver = ReadU16(b, 2);
        ushort size  = ReadU16(b, 4), crc = ReadU16(b, 6);

        if (magic != Magic) return (false, $"magic 0x{magic:X4} != 0x{Magic:X4}");
        if (ver != Version) return (false, $"配置版本 0x{ver:X4} != 0x{Version:X4}（固件与上位机需同步升级）");
        if (size != Size)   return (false, $"size {size} != {Size}");

        // 两个摇杆的死区都查（0x0004 起各摇杆独立，范围放宽到 0~60）
        for (int s = 0; s < StickCount; s++)
            if (b[8 + s * 8] > 60) return (false, $"摇杆{s} 死区 {b[8 + s * 8]}% 超出 0~60%");

        // 偏移 57/58（原宏总开关/上电默认）现在是保留字节，不再校验 ——
        // 老配置里存着旧值也要能正常加载。

        ushort calc = Crc16.Compute(b[CrcOffset..]);
        if (calc != crc) return (false, $"CRC 0x{crc:X4} != 算出 0x{calc:X4}");

        return (true, $"magic/version/size/CRC 全部通过 (CRC 0x{crc:X4})");
    }

    private static void WriteU16(byte[] b, int o, ushort v)
    {
        b[o] = (byte)(v & 0xFF);
        b[o + 1] = (byte)(v >> 8);
    }

    private static ushort ReadU16(ReadOnlySpan<byte> b, int o)
        => (ushort)(b[o] | (b[o + 1] << 8));
}
