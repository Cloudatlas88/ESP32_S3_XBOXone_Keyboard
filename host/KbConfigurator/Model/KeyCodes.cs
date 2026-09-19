namespace KbConfigurator.Model;

/// <summary>一个可映射的目标键</summary>
public sealed record KeyOption(string Name, byte Code, byte Modifier = 0)
{
    public override string ToString() => Name;
}

/// <summary>
/// HID 键盘 Usage ID 表（USB HID Usage Tables, Keyboard/Keypad page 0x07）。
/// 修饰键用 Modifier 位表示（对应固件的 MOD_* 宏）。
/// </summary>
public static class KeyCodes
{
    public const byte MOD_LCTRL  = 0x01;
    public const byte MOD_LSHIFT = 0x02;
    public const byte MOD_LALT   = 0x04;
    public const byte MOD_LGUI   = 0x08;

    /// <summary>「不映射」—— 固件里用 keycode = 0 表示</summary>
    public static readonly KeyOption None = new("（不映射）", 0x00);

    public static readonly IReadOnlyList<KeyOption> All = Build();

    private static List<KeyOption> Build()
    {
        var l = new List<KeyOption> { None };

        // 字母
        for (char c = 'A'; c <= 'Z'; c++)
            l.Add(new KeyOption(c.ToString(), (byte)(0x04 + (c - 'A'))));

        l.Add(new KeyOption("1", 0x1E)); l.Add(new KeyOption("2", 0x1F));
        l.Add(new KeyOption("3", 0x20)); l.Add(new KeyOption("4", 0x21));
        l.Add(new KeyOption("5", 0x22)); l.Add(new KeyOption("6", 0x23));
        l.Add(new KeyOption("7", 0x24)); l.Add(new KeyOption("8", 0x25));
        l.Add(new KeyOption("9", 0x26)); l.Add(new KeyOption("0", 0x27));

        l.Add(new KeyOption("Enter",     0x28));
        l.Add(new KeyOption("Esc",       0x29));
        l.Add(new KeyOption("Backspace", 0x2A));
        l.Add(new KeyOption("Tab",       0x2B));
        l.Add(new KeyOption("Space",     0x2C));
        l.Add(new KeyOption("-",         0x2D));
        l.Add(new KeyOption("=",         0x2E));
        l.Add(new KeyOption("[",         0x2F));
        l.Add(new KeyOption("]",         0x30));
        l.Add(new KeyOption("\\",        0x31));
        l.Add(new KeyOption(";",         0x33));
        l.Add(new KeyOption("'",         0x34));
        l.Add(new KeyOption("`",         0x35));
        l.Add(new KeyOption(",",         0x36));
        l.Add(new KeyOption(".",         0x37));
        l.Add(new KeyOption("/",         0x38));

        l.Add(new KeyOption("CapsLock",  0x39));
        for (int i = 0; i < 12; i++)
            l.Add(new KeyOption($"F{i + 1}", (byte)(0x3A + i)));

        l.Add(new KeyOption("PrintScreen", 0x46));
        l.Add(new KeyOption("ScrollLock",  0x47));
        l.Add(new KeyOption("Pause",       0x48));
        l.Add(new KeyOption("Insert",      0x49));
        l.Add(new KeyOption("Home",        0x4A));
        l.Add(new KeyOption("PageUp",      0x4B));
        l.Add(new KeyOption("Delete",      0x4C));
        l.Add(new KeyOption("End",         0x4D));
        l.Add(new KeyOption("PageDown",    0x4E));

        // 方向键：名字里带上英文，这样用户敲 down / 下 / ↓ 都能搜到
        // （原来只写「↓ 下」，规范化后只剩"下"，敲 down 搜不出来）
        l.Add(new KeyOption("→ 右 (Right)", 0x4F));
        l.Add(new KeyOption("← 左 (Left)",  0x50));
        l.Add(new KeyOption("↓ 下 (Down)",  0x51));
        l.Add(new KeyOption("↑ 上 (Up)",    0x52));

        l.Add(new KeyOption("NumLock", 0x53));

        // 组合键（用 Modifier 位表示，配合单个键码使用）
        //
        // ⚠️ 参数顺序是 (Name, Code, Modifier) —— 这里**曾经写反过**：
        //    写成 new KeyOption("Ctrl+A", MOD_LCTRL, 0x04)，
        //    结果 Code=0x01、Modifier=0x04，
        //    选「Ctrl+A」实际会发出【键码 0x01(ErrorRollOver) + 左Alt】这种垃圾。
        //    位置参数编译期查不出来，所以 --uilogic 里加了"所有条目必须格式合法"的断言兜底。
        l.Add(new KeyOption("Ctrl+A", 0x04, MOD_LCTRL));
        l.Add(new KeyOption("Ctrl+C", 0x06, MOD_LCTRL));
        l.Add(new KeyOption("Ctrl+V", 0x19, MOD_LCTRL));
        l.Add(new KeyOption("Ctrl+X", 0x1B, MOD_LCTRL));
        l.Add(new KeyOption("Ctrl+Z", 0x1D, MOD_LCTRL));
        l.Add(new KeyOption("Ctrl+S", 0x16, MOD_LCTRL));
        l.Add(new KeyOption("Shift+↑", 0x52, MOD_LSHIFT));
        l.Add(new KeyOption("Alt+Tab", 0x2B, MOD_LALT));

        return l;
    }

    public static KeyOption Find(byte code, byte mod = 0)
        => All.FirstOrDefault(k => k.Code == code && k.Modifier == mod) ?? None;

    /// <summary>合法的 HID 键盘 Usage ID 范围（本工具支持的键都在这个区间里）</summary>
    public const byte MinKeycode = 0x04;
    public const byte MaxKeycode = 0xE7;

    /// <summary>合法的修饰键位（按位或的组合也算合法）</summary>
    private const byte ValidModifiers = MOD_LCTRL | MOD_LSHIFT | MOD_LALT | MOD_LGUI;

    /// <summary>
    /// 自检：键表里每一条都必须格式合法。
    ///
    /// ★ 加这个是因为踩过一次：组合键那几项的构造参数写反了
    ///   （<c>new KeyOption("Ctrl+A", MOD_LCTRL, 0x04)</c> 把 Modifier 传进了 Code 位置），
    ///   结果键码变成 0x01、修饰键变成 0x04 —— 选「Ctrl+A」会发出一串垃圾，
    ///   而且编译器完全查不出来（位置参数顺序错了类型却都对）。
    ///   有了这条断言，以后再有类似写反会当场被测出来。
    /// </summary>
    public static IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        foreach (var k in All)
        {
            if (k.Code != 0 && (k.Code < MinKeycode || k.Code > MaxKeycode))
                problems.Add($"「{k.Name}」键码 0x{k.Code:X2} 不在 HID 键盘 Usage 范围 " +
                             $"0x{MinKeycode:X2}~0x{MaxKeycode:X2} 内（参数可能写反了）");

            if ((k.Modifier & ~ValidModifiers) != 0)
                problems.Add($"「{k.Name}」修饰键 0x{k.Modifier:X2} 含非法位");
        }

        // 组合键必须真的"有修饰键"，且键码是可打印键
        foreach (var k in All.Where(x => x.Name.Contains('+')))
        {
            if (k.Modifier == 0)
                problems.Add($"「{k.Name}」名字里有 + 但修饰键是 0（参数顺序可能写反）");
            if (k.Code < MinKeycode || k.Code > MaxKeycode)
                problems.Add($"「{k.Name}」键码 0x{k.Code:X2} 非法（参数顺序可能写反）");
        }

        return problems;
    }

    /// <summary>
    /// 按关键字搜索可映射的键。
    ///
    /// ★ 为什么要这个：原来 110 项平铺在下拉里，找 <c>PageDown</c> 得翻很久。
    ///   现在可以直接输入过滤，几种常见写法都认：
    ///     `pgdn` / `pagedown` / `page down` / `down`  → PageDown
    ///     `↓` / `下` / `down`                          → ↓ 下 (Down)
    ///     `a`     → 字母 A
    ///     `ctrl`  → 所有 Ctrl+? 组合
    ///   关键字为空时返回全部。
    ///
    /// ★ 一条重要行为：**搜不到就返回全部**，而不是返回空列表。
    ///   如果敲错一个字母下拉就空了，用户会以为"按键列表坏了"。
    ///   所以调用方看到结果数 == All.Count 时，要理解成"没筛出来"而不是"筛出来这么多"。
    /// </summary>
    public static IReadOnlyList<KeyOption> Search(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return All;

        var q = Normalize(filter);
        if (q.Length == 0) return All;

        var hits = All.Where(k => Matches(k.Name, q)).ToList();
        return hits.Count > 0 ? hits : All;
    }

    private static bool Matches(string name, string q)
    {
        var n = Normalize(name);
        if (n.Contains(q)) return true;

        // 常见缩写：用户记得住的手感写法，不可能记得住确切拼法
        foreach (var (full, shorts) in Shorthands)
        {
            if (!n.Contains(full)) continue;
            if (full.Contains(q)) return true;
            foreach (var s in shorts)
                if (q.Contains(s) || s.Contains(q)) return true;
        }
        return false;
    }

    private static readonly (string Full, string[] Shorts)[] Shorthands =
    {
        ("pagedown",    new[] { "pgdn", "pgdown" }),
        ("pageup",      new[] { "pgup" }),
        ("backspace",   new[] { "bs", "back" }),
        ("capslock",    new[] { "caps" }),
        ("printscreen", new[] { "prtsc", "print" }),
        ("scrolllock",  new[] { "scroll" }),
        ("numlock",     new[] { "num" }),
        ("delete",      new[] { "del" }),
        ("insert",      new[] { "ins" }),
        ("escape",      new[] { "esc" }),
        ("enter",       new[] { "return" }),
        ("space",       new[] { "spc" }),
    };

    /// <summary>
    /// 规范化：把箭头符号换成方向单词、去掉空格/连字符/括号，转小写。
    ///
    /// ★ 箭头**必须**先换成单词再处理 —— 早先直接把它们当"标点剥掉"，
    ///   结果搜「↓」变成了搜空字符串，退回全部 88 项，
    ///   看起来"搜到了"其实过滤根本没生效（测试里就是这么假通过的）。
    /// </summary>
    private static string Normalize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 8);
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '↑': sb.Append("up");    continue;
                case '↓': sb.Append("down");  continue;
                case '←': sb.Append("left");  continue;
                case '→': sb.Append("right"); continue;
            }

            if (char.IsWhiteSpace(ch)) continue;
            if (ch is '-' or '_' or '(' or ')' or '（' or '）' or '+' or '/' or '.') continue;
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }
}
