namespace KbConfigurator.Protocol;

/// <summary>
/// 槽位（slot）相关的公共词汇：槽位号 ↔ 配置字段、方向位、实时高亮状态。
///
/// ══════════════════════════════════════════════════════════════════
/// ★ 0x0004 起这里是**恒等映射** —— "位号即槽位号"。
///
///   0x0003 时界面按 24 槽位设计、而固件只有 5 个输入，中间需要一层适配
///   （`Mode.Legacy5` 把固件 bit0..4 映射到槽位 0/1/2/3/10，方向映射到 16..19）。
///   现在固件的槽位定义和 layout.json 完全一致，那层适配整个不需要了。
///
///   所以这个类现在很薄：**它存在的意义是给"槽位"这套词汇一个落脚点**
///   （方向位、高亮状态、槽位↔配置字段的读写），
///   而不是像以前那样承担一次映射转换。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public static class SlotMap
{
    /// <summary>槽位总数（= 固件 SLOT_COUNT = layout.json 的 slots 长度）</summary>
    public const int SlotCount = KbConfig.SlotCount;

    /// <summary>方向位：bit0 上 / bit1 下 / bit2 左 / bit3 右（与固件 DIRBIT_* 一致）</summary>
    public const int DirUp = 1, DirDown = 2, DirLeft = 4, DirRight = 8;

    /// <summary>摇杆方向槽位的基址（与固件 DIRBASE_* 一致）</summary>
    public const int BaseDpad = 12, BaseLstick = 16, BaseRstick = 20;

    /// <summary>
    /// 26 个槽位的显示名 —— **全 UI 唯一的一套叫法**。
    ///
    /// ★ 叫法要和按键布局页上映射牌的名字一致（"A 键" / "LB 肩键" / "LT 扳机"），
    ///   否则同一台设备在宏触发绑定里叫 "LB"、在布局页叫 "LB 肩键"，
    ///   用户得在脑子里做一次翻译（这就是"宏触发绑定里的按键要按最终稿更新"的由来）。
    ///   方向类槽位带上箭头，因为一个摇杆/十字键占 4 个槽位，光写"十字键"分不清是哪个方向。
    ///
    /// ★ 顺序必须与固件 slot_idx_t 一致。
    /// </summary>
    public static readonly string[] SlotNames =
    {
        "A 键", "B 键", "X 键", "Y 键",
        "LB 肩键", "RB 肩键",
        "View 键", "Menu 键", "Xbox 键", "Share 键",
        "L3", "R3",
        "十字键 ↑", "十字键 ↓", "十字键 ←", "十字键 →",
        "左摇杆 ↑", "左摇杆 ↓", "左摇杆 ←", "左摇杆 →",
        "右摇杆 ↑", "右摇杆 ↓", "右摇杆 ←", "右摇杆 →",
        "LT 扳机", "RT 扳机",
    };

    public static string NameOf(int slot)
        => slot >= 0 && slot < SlotNames.Length ? SlotNames[slot] : $"槽位{slot}";

    // ══════════════════════════════════════════════════════════════
    //  一帧输入算出来的高亮状态
    // ══════════════════════════════════════════════════════════════

    public sealed class Highlights
    {
        /// <summary>当前处于"按下"状态的槽位</summary>
        public readonly HashSet<int> Active = new();

        /// <summary>方向组（layout.json 里的项 id）→ 4 位方向掩码</summary>
        public readonly Dictionary<string, int> DirBits = new();

        public bool MacroBusy;
        public bool Valid;

        public bool IsSlotActive(int slot) => Active.Contains(slot);

        public int DirsOf(string itemId)
            => DirBits.TryGetValue(itemId, out var v) ? v : 0;

        public void Clear()
        {
            Active.Clear();
            DirBits.Clear();
            MacroBusy = false;
            Valid = false;
        }

        public int ActiveCount => Active.Count;
    }

    /// <summary>
    /// 把一帧 <see cref="InputState"/> 翻译成高亮状态。
    /// <paramref name="into"/> 会被**清空后重填**。
    ///
    /// ★ 这里现在只是"把 24 位位图拆成一个个槽位号 + 三组方向"，
    ///   没有映射转换 —— 固件报的就是槽位号。
    /// </summary>
    public static void Fill(InputState st, Highlights into)
    {
        into.Clear();
        if (!st.Valid) return;

        into.Valid = true;
        into.MacroBusy = st.MacroBusy;

        for (int i = 0; i < SlotCount; i++)
            if (st.Slot(i)) into.Active.Add(i);

        // 方向位直接取固件报的三组（十字键 / 左摇杆 / 右摇杆）
        into.DirBits["dpad"]   = st.DirsDpad;
        into.DirBits["lstick"] = st.DirsLstick;
        into.DirBits["rstick"] = st.DirsRstick;
    }

    /// <summary>从槽位位图取某 4 个连续槽位对应的方向位（与固件 slots_to_dirs 同义）</summary>
    public static int DirsFromSlots(uint slots, int baseSlot)
    {
        int d = 0;
        if ((slots & (1u << (baseSlot + 0))) != 0) d |= DirUp;
        if ((slots & (1u << (baseSlot + 1))) != 0) d |= DirDown;
        if ((slots & (1u << (baseSlot + 2))) != 0) d |= DirLeft;
        if ((slots & (1u << (baseSlot + 3))) != 0) d |= DirRight;
        return d;
    }

    public static int Pack(bool up, bool down, bool left, bool right)
        => (up ? DirUp : 0) | (down ? DirDown : 0)
         | (left ? DirLeft : 0) | (right ? DirRight : 0);

    /// <summary>把方向掩码写成"上左下右"这样的可读文字</summary>
    public static string DirText(int bits)
    {
        if (bits == 0) return "—";
        var s = "";
        if ((bits & DirUp) != 0) s += "上";
        if ((bits & DirDown) != 0) s += "下";
        if ((bits & DirLeft) != 0) s += "左";
        if ((bits & DirRight) != 0) s += "右";
        return s;
    }

    // ══════════════════════════════════════════════════════════════
    //  槽位 ↔ 配置字段（0x0004 起是恒等：槽位 i ↔ cfg.Buttons[i]）
    // ══════════════════════════════════════════════════════════════

    /// <summary>槽位号是否有效</summary>
    public static bool SlotSupported(int slot) => slot >= 0 && slot < SlotCount;

    /// <summary>读某个槽位当前的映射（键码 + 修饰键）</summary>
    public static (byte Code, byte Mod) ReadSlot(KbConfig cfg, int slot)
        => SlotSupported(slot) ? (cfg.Buttons[slot].Keycode, cfg.Buttons[slot].Modifiers) : ((byte)0, (byte)0);

    /// <summary>
    /// 写某个槽位。越界**直接拒绝并返回 false**，不改动任何字节。
    /// （24 个槽位全都是普通按键项，所以"修饰键存不了"这类限制已经不存在了。）
    /// </summary>
    public static bool WriteSlot(KbConfig cfg, int slot, byte code, byte mod)
    {
        if (!SlotSupported(slot)) return false;
        cfg.Buttons[slot].Keycode   = code;
        cfg.Buttons[slot].Modifiers = mod;
        return true;
    }

    /// <summary>
    /// 这个槽位有没有「启用 / 禁用」开关。
    /// ★ 0x0004 起 24 个槽位**全都是普通按键项**，都有 flags 字段，所以全都能禁启用。
    ///   0x0003 时摇杆方向键没有这个字段，才需要单独判断。
    /// </summary>
    public static bool SlotSupportsEnable(int slot) => SlotSupported(slot);

    public static bool ReadSlotEnabled(KbConfig cfg, int slot)
        => !SlotSupported(slot) || cfg.Buttons[slot].Enabled;

    public static bool WriteSlotEnabled(KbConfig cfg, int slot, bool on)
    {
        if (!SlotSupported(slot)) return false;
        cfg.Buttons[slot].Enabled = on;
        return true;
    }

    /// <summary>
    /// 某个槽位绑了宏的话返回说明文字；否则空串。
    /// （绑定本身在「宏编辑」页改，这里只提示 —— 否则用户会奇怪
    ///   "我明明设了按键，怎么按下去出来的是宏"。）
    /// </summary>
    public static string MacroNoteOf(KbConfig cfg, int slot)
    {
        if (!SlotSupported(slot)) return "";
        var b = cfg.Buttons[slot];
        if (b.UsesMacro && b.MacroId < KbConfig.MacroMax)
            return $"★ 已绑定宏 {b.MacroId + 1}（本行映射被忽略）";
        return "";
    }

    /// <summary>
    /// 摇杆设置（死区/反向）挂在哪个热区项上。
    /// ★ 0x0004 起两个摇杆各有一套，所以 lstick / rstick 都能改；
    ///   十字键不是模拟量，没有死区概念。
    /// </summary>
    public static bool ItemHasStickSettings(string itemId)
        => itemId is "lstick" or "rstick";

    /// <summary>热区项 id → 摇杆下标（不是摇杆项返回 -1）</summary>
    public static int StickIndexOfItem(string itemId) => itemId switch
    {
        "lstick" => 0,
        "rstick" => 1,
        _ => -1,
    };

    /// <summary>
    /// 把废弃的 trigger 字段固化为 0。
    /// 实体键盘只有"按下保持 / 松开抬起"一种逻辑，没有可配的"触发方式"，
    /// 固件也已不读这个字段 —— 界面上留个会被忽略的值只会让人误解。
    /// </summary>
    public static void NormalizeDeprecatedFields(KbConfig cfg)
    {
        foreach (var b in cfg.Buttons) b.Trigger = 0;
    }
}
