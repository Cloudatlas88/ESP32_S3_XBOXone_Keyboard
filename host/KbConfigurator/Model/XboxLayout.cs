using System.Text.Json;

namespace KbConfigurator.Model;

/// <summary>
/// 手柄示意图上的一个交互项（热区）。
///
/// 三种 kind：
///   · <c>single</c> —— 一个按键，占 1 个槽位（图上有一个引线端点）
///   · <c>dir4</c>   —— 十字键/摇杆，占 4 个方向槽位（图上只有一个端点）
///   · <c>badge</c>  —— 图上是空白处，没有引线端点（L3 / R3），画成徽章，占 1 个槽位
///
/// 坐标同时保存**归一化**（0~1，用于随窗口缩放定位）和**像素**（原图坐标，
/// 用于校准模式和排查）。归一化坐标才是渲染用的那份。
/// </summary>
public sealed class LayoutItem
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string DiagramLabel { get; set; } = "";

    /// <summary>单槽位项的槽位号；dir4 没有这个字段，所以是可空的</summary>
    public int? Slot { get; set; }

    /// <summary>该项占用的全部槽位（dir4 是 4 个）</summary>
    public List<int> Slots { get; set; } = new();

    /// <summary>归一化坐标，渲染用</summary>
    public double[] Dot { get; set; } = new double[2];

    /// <summary>原图像素坐标，仅用于校准/排查</summary>
    public int[] DotPx { get; set; } = new int[2];

    /// <summary>图上原文字标签框 [x0,y0,x1,y1]，校准模式用来对齐</summary>
    public int[] LabelPx { get; set; } = Array.Empty<int>();

    /// <summary>badge 项挂在哪个摇杆上（"lstick" / "rstick"）</summary>
    public string? Attach { get; set; }

    public bool IsDir4 => Kind == "dir4";
    public bool IsBadge => Kind == "badge";

    public override string ToString() => $"{Id}({Kind}, slots=[{string.Join(",", Slots)}])";
}

/// <summary>
/// 手柄示意图的布局定义，从 <c>Assets/layout.json</c> 读入。
///
/// ★ 为什么坐标放 JSON 而不是写死在代码里：
///   坐标是**程序从图上量出来的**（见 tools/gen_xbox_layout.py），
///   而"控件放在这里好不好看"只有人能看到。分开之后，
///   换图或微调坐标不用改代码、不用重编译 —— 校准模式直接写回这个文件。
/// </summary>
public sealed class XboxLayout
{
    public int Version { get; set; }
    public string Image { get; set; } = "";
    public int ImageW { get; set; }
    public int ImageH { get; set; }
    public string SourceNote { get; set; } = "";

    /// <summary>槽位名字表，下标 = 槽位号（固件必须与之一致）</summary>
    public List<string> Slots { get; set; } = new();

    public List<LayoutItem> Items { get; set; } = new();

    /// <summary>图上画了、但当前不接线的项（LT / RT）—— 保留热区以便将来直接启用</summary>
    public List<LayoutItem> Deferred { get; set; } = new();

    public IEnumerable<LayoutItem> All => Items.Concat(Deferred);

    private static readonly JsonSerializerOptions Opt = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOpt = new()
    {
        WriteIndented = true,
        // 不转义中文：默认会把中文写成 \uXXXX，能用但 git diff 里完全没法看
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static XboxLayout Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<XboxLayout>(json, Opt)
               ?? throw new InvalidDataException($"{path} 解析结果为空");
    }

    /// <summary>从输出目录的 Assets/ 找布局文件</summary>
    public static string DefaultPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "layout.json");

    /// <summary>
    /// 出厂坐标备份的路径（「恢复默认」的参照）。
    /// ★ 由 tools/gen_xbox_layout.py 与 layout.json 一起生成，内容完全相同。
    ///   没有这份备份的话，校准拖动之后就没有可回去的地方了。
    /// </summary>
    public static string DefaultBackupPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "layout.default.json");

    /// <summary>
    /// 写回 JSON（布局校准用）。
    ///
    /// ★ 先写临时文件再替换：这个文件是界面坐标的唯一来源，
    ///   写到一半崩了会留下半个 JSON，下次启动直接解析失败、整页打不开。
    ///   替换是原子的，最坏情况也只是保留旧内容。
    /// </summary>
    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(this, WriteOpt);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json, new System.Text.UTF8Encoding(false));

        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }

    // ══════════════════════════════════════════════════════════════
    //  校准用的快照 / 还原
    // ══════════════════════════════════════════════════════════════

    /// <summary>一项坐标的可还原快照</summary>
    public sealed record CoordSnapshot(LayoutItem Item, double[] Dot, int[] LabelPx);

    /// <summary>把当前坐标都拍一份，供「取消」还原</summary>
    public List<CoordSnapshot> Snapshot()
        => All.Select(i => new CoordSnapshot(i, (double[])i.Dot.Clone(),
                                                (int[])i.LabelPx.Clone())).ToList();

    /// <summary>还原到快照</summary>
    public static void Restore(IEnumerable<CoordSnapshot> snap)
    {
        foreach (var s in snap)
        {
            s.Item.Dot = (double[])s.Dot.Clone();
            s.Item.LabelPx = (int[])s.LabelPx.Clone();
        }
    }

    /// <summary>把坐标恢复到出厂（重新读备份文件的内容，但保留当前实例）</summary>
    public bool ResetToFactoryDefaults(out string reason)
    {
        reason = "";
        var backup = DefaultBackupPath;
        if (!File.Exists(backup))
        {
            reason = $"找不到出厂坐标备份：{backup}";
            return false;
        }

        XboxLayout def;
        try { def = Load(backup); }
        catch (Exception ex) { reason = $"出厂备份解析失败：{ex.Message}"; return false; }

        // 按 id 匹配着还原，而不是整体替换 —— 调用方（界面）已经持有
        // 当前这些 LayoutItem 实例，整体替换会让它们变成孤儿
        int n = 0;
        foreach (var cur in All)
        {
            var d = def.All.FirstOrDefault(x => x.Id == cur.Id);
            if (d is null) continue;
            cur.Dot = (double[])d.Dot.Clone();
            cur.LabelPx = (int[])d.LabelPx.Clone();
            n++;
        }

        reason = $"已恢复 {n} 项的出厂坐标";
        return true;
    }

    /// <summary>布局文件里 image 字段指向的图片的绝对路径</summary>
    public string ResolveImagePath()
    {
        var rel = Image.Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(AppContext.BaseDirectory, rel);
    }

    /// <summary>
    /// 自检。**这个方法必须真的会失败** —— 校验器永远返回 ok 等于没有。
    /// 覆盖：版本、槽位完整性（0..N-1 各一次，不能多不能少）、id 唯一、
    /// 坐标范围、图片尺寸。
    /// </summary>
    public (bool Ok, string Reason) Validate()
    {
        if (Version != 1) return (false, $"布局版本不支持：{Version}（期望 1）");
        if (ImageW <= 0 || ImageH <= 0) return (false, $"图片尺寸非法：{ImageW}×{ImageH}");
        if (string.IsNullOrWhiteSpace(Image)) return (false, "image 字段为空");
        if (Slots.Count == 0) return (false, "slots 为空");
        if (Items.Count == 0) return (false, "items 为空");

        var dup = Items.Select(i => i.Id).GroupBy(s => s)
                       .Where(gr => gr.Count() > 1).Select(gr => gr.Key).ToList();
        if (dup.Count > 0) return (false, $"id 重复：{string.Join(",", dup)}");

        foreach (var it in Items)
        {
            if (it.Dot.Length != 2) return (false, $"{it.Id}: dot 不是 2 个分量");
            if (it.Dot[0] < 0 || it.Dot[0] > 1 || it.Dot[1] < 0 || it.Dot[1] > 1)
                return (false, $"{it.Id}: 归一化坐标越界 ({it.Dot[0]},{it.Dot[1]})");
            if (it.Slots.Count == 0) return (false, $"{it.Id}: 没有分配槽位");
            foreach (var s in it.Slots)
                if (s < 0 || s >= Slots.Count)
                    return (false, $"{it.Id}: 槽位 {s} 超出 0..{Slots.Count - 1}");
            if (it.IsDir4 && it.Slots.Count != 4)
                return (false, $"{it.Id}: dir4 项应有 4 个槽位，实际 {it.Slots.Count}");
            if (!it.IsDir4 && it.Slots.Count != 1)
                return (false, $"{it.Id}: {it.Kind} 项应有 1 个槽位，实际 {it.Slots.Count}");
        }

        // ★ 关键断言：所有槽位必须被**恰好覆盖一次**。
        //   items 里漏掉 L3/R3 时只覆盖了 22/24，就是这条抓出来的。
        var covered = Items.SelectMany(i => i.Slots).OrderBy(s => s).ToList();
        var missing = Enumerable.Range(0, Slots.Count).Except(covered).ToList();
        var extra = covered.Except(Enumerable.Range(0, Slots.Count)).ToList();
        if (extra.Count > 0) return (false, $"出现了不存在的槽位：{string.Join(",", extra)}");
        if (missing.Count > 0)
            return (false, $"槽位覆盖不全，缺 {missing.Count} 个：{string.Join(",", missing)}");
        if (covered.Count != Slots.Count)
            return (false, $"槽位被重复分配：分配了 {covered.Count} 次，只有 {Slots.Count} 个槽位");

        return (true, $"{Items.Count} 个交互项 / {Slots.Count} 个槽位 / "
                    + $"{Deferred.Count} 个暂缓项，槽位 0..{Slots.Count - 1} 各覆盖一次");
    }
}
