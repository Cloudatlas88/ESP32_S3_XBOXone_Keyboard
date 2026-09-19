using KbConfigurator.Model;
using KbConfigurator.Protocol;
using KbConfigurator.Ui;
using KbConfigurator.Views;

namespace KbConfigurator;

/// <summary>
/// 「按键布局」页验收 —— 用法：KbConfigurator.exe --xlayout
///
/// 覆盖三块最容易出错、又最难靠肉眼发现的东西：
///
///   1. **布局数据**：layout.json 能被解析、自检通过、槽位完整覆盖。
///      槽位漏一个（比如 L3/R3）在界面上完全看不出来，只会"某个键按了没反应"。
///
///   2. **坐标映射**：信箱式缩放的往返一致性。
///      写错一个偏移的表现是"图看着对、热区整体偏一截"，只在特定窗口比例下暴露。
///
///   3. **命中判定**：每个热区必须能被自己的中心点命中。
///      这条是**逐项**断言全部 15 个热区，不是抽查 —— 而且专门加了
///      "确实遍历到了全部热区"的断言防止空跑通过。
/// </summary>
internal static class XboxLayoutTest
{
    public static int Run()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        int pass = 0, fail = 0;

        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"
                            + (detail.Length > 0 ? "  —— " + detail : ""));
            if (ok) pass++; else fail++;
        }

        Console.WriteLine("════════ 「按键布局」页验收 ════════\n");

        // ─────────── 1. 布局数据 ───────────
        Console.WriteLine("── 1. 布局数据 (Assets/layout.json)");

        var path = XboxLayout.DefaultPath;
        Check("layout.json 已复制到输出目录", File.Exists(path), path);
        if (!File.Exists(path))
        {
            Console.WriteLine($"\n✗ 无法继续：{path} 不存在。" +
                              "检查 csproj 里的 Assets\\**\\* 复制规则。");
            return 1;
        }

        XboxLayout layout;
        try
        {
            layout = XboxLayout.Load(path);
        }
        catch (Exception ex)
        {
            Check("layout.json 解析", false, ex.Message);
            return 1;
        }
        Check("layout.json 解析", true);

        var (ok, reason) = layout.Validate();
        Check("自检通过（槽位完整覆盖）", ok, reason);

        Check("交互项数量 = 17（LT/RT 已从暂缓提升为真实项）", layout.Items.Count == 17,
                  $"实际 {layout.Items.Count}");
        Check("槽位数量 = 26", layout.Slots.Count == 26, $"实际 {layout.Slots.Count}");
        Check("暂缓项 = 0（LT/RT 已落地为真实槽位 24/25）", layout.Deferred.Count == 0,
              string.Join(",", layout.Deferred.Select(d => d.Id)));

        // 单键 / 方向类 / 徽章的数量
        int nSingle = layout.Items.Count(i => i.Kind == "single");
        int nDir4 = layout.Items.Count(i => i.IsDir4);
        int nBadge = layout.Items.Count(i => i.IsBadge);
        Check("12 个普通单键（含 LT/RT）", nSingle == 12, $"实际 {nSingle}");
        Check("3 个方向类（十字键 + 双摇杆）", nDir4 == 3, $"实际 {nDir4}");
        Check("2 个徽章（L3 / R3）", nBadge == 2, $"实际 {nBadge}");
        Check("槽位总数 = 12*1 + 3*4 + 2*1 = 26",
              nSingle + nDir4 * 4 + nBadge == layout.Slots.Count);

        // ★ 自检器必须真的会失败：把槽位挖掉一个，Validate 必须报错
        {
            var broken = XboxLayout.Load(path);
            broken.Items.RemoveAll(i => i.Id == "l3");
            var (bok, breason) = broken.Validate();
            Check("防假绿：挖掉 L3 后自检必须不通过",
                  !bok && breason.Contains("槽位覆盖不全"),
                  bok ? "竟然通过了！" : breason);
        }

        // ─────────── 2. ABXY 几何不变量 ───────────
        // 引线追踪校正后 ABXY 应当是标准菱形。这是坐标数据的强回归断言：
        // 一旦坐标被改错（比如又换回早期那套错的对应关系），这里立刻红。
        Console.WriteLine("\n── 2. ABXY 菱形几何不变量");
        {
            LayoutItem Get(string id) => layout.Items.First(i => i.Id == id);
            var a = Get("a").Dot; var b = Get("b").Dot;
            var x = Get("x").Dot; var y = Get("y").Dot;

            Check("Y 在 A 正上方", Math.Abs(y[0] - a[0]) < 0.01 && y[1] < a[1],
                  $"Y=({y[0]:F4},{y[1]:F4}) A=({a[0]:F4},{a[1]:F4})");
            Check("X 在 B 正左方", Math.Abs(x[1] - b[1]) < 0.01 && x[0] < b[0],
                  $"X=({x[0]:F4},{x[1]:F4}) B=({b[0]:F4},{b[1]:F4})");
            double dx = b[0] - x[0], dy = a[1] - y[1];
            Check("菱形横竖跨度接近（比例 0.5~2.0）",
                  dx / dy > 0.5 && dx / dy < 2.0, $"横 {dx:F4} / 竖 {dy:F4}");

            // 左右摇杆分别在图的左半/右半；十字键在中下
            Check("左摇杆在左半区", Get("lstick").Dot[0] < 0.5);
            Check("右摇杆在右半区", Get("rstick").Dot[0] > 0.5);
            Check("十字键在左下方", Get("dpad").Dot[0] < 0.5 && Get("dpad").Dot[1] > 0.5);
        }

        // ─────────── 3. 坐标映射 ───────────
        Console.WriteLine("\n── 3. 信箱式坐标映射 DiagramMap");
        {
            // 宽图放进窄控件：左右贴边、上下留边
            var wide = new DiagramMap(2400, 1350, 800, 800);
            Check("宽图进窄控件：左右贴边",
                  Math.Abs(wide.OffX) < 0.01f, $"OffX={wide.OffX}");
            Check("宽图进窄控件：上下留黑边",
                  wide.OffY > 1 && Math.Abs(wide.DrawH + wide.OffY * 2 - 800) < 0.5f,
                  $"OffY={wide.OffY:F2} DrawH={wide.DrawH:F2}");
            Check("宽图进窄控件：宽高比保持",
                  Math.Abs(wide.DrawW / wide.DrawH - 2400f / 1350f) < 1e-4);

            // 高图放进宽控件（对调），确保不是只有一条分支对
            var tall = new DiagramMap(1350, 2400, 900, 600);
            Check("高图进宽控件：上下贴边", Math.Abs(tall.OffY) < 0.01f, $"OffY={tall.OffY}");
            Check("高图进宽控件：左右留黑边", tall.OffX > 1, $"OffX={tall.OffX:F2}");

            // 等比且刚好放下：scale 应为 1，无黑边
            var exact = new DiagramMap(1000, 500, 1000, 500);
            Check("尺寸正好相等时 scale=1 且无黑边",
                  Math.Abs(exact.Scale - 1f) < 1e-5
                  && Math.Abs(exact.OffX) < 1e-4 && Math.Abs(exact.OffY) < 1e-4);

            // 退化尺寸不能崩、也不能算出 NaN
            var zero = new DiagramMap(2400, 1350, 0, 0);
            Check("控件尺寸为 0 时映射标记为无效", !zero.IsValid);

            // 四个角：归一化 (0,0)/(1,1) 必须落在 ImageRect 的角上
            var m = new DiagramMap(2400, 1350, 1180, 700);
            var r = m.ImageRect;
            var tl = m.ToScreen(0, 0);
            var br = m.ToScreen(1, 1);
            Check("(0,0) -> 图片左上角",
                  Math.Abs(tl.X - r.Left) < 0.01f && Math.Abs(tl.Y - r.Top) < 0.01f);
            Check("(1,1) -> 图片右下角",
                  Math.Abs(br.X - r.Right) < 0.01f && Math.Abs(br.Y - r.Bottom) < 0.01f);

            // 往返一致性（在多种控件尺寸下都验）
            int roundTrips = 0, rtBad = 0;
            foreach (var (cw, ch) in new[] { (400, 300), (800, 800), (1180, 700), (1920, 1080), (300, 900) })
            {
                var mm = new DiagramMap(2400, 1350, cw, ch);
                foreach (var it in layout.All)
                {
                    var s = mm.ToScreen(it.Dot[0], it.Dot[1]);
                    var back = mm.ToNormalized(s.X, s.Y);
                    roundTrips++;
                    if (Math.Abs(back.X - it.Dot[0]) > 1e-3 || Math.Abs(back.Y - it.Dot[1]) > 1e-3)
                        rtBad++;
                }
            }
            Check($"往返一致（{roundTrips} 次，5 种控件尺寸）", rtBad == 0 && roundTrips > 0,
                  rtBad == 0 ? $"{layout.All.Count()} 热区 × 5 尺寸" : $"{rtBad} 次超差");
            // 防假绿：断言"确实比对了这么多次"。
            // ★ 注意 detail 文案别写成失败语气 —— 上一版这里写成 "85 次 != 17×5"，
            //   明明是通过却在输出里看着像报错。
            Check("防假绿：往返测试确实覆盖了全部热区",
                  roundTrips == layout.All.Count() * 5,
                  $"实际比对 {roundTrips} 次，期望 {layout.All.Count()} × 5 = {layout.All.Count() * 5}");
        }

        // ─────────── 4. 热区不越界 ───────────
        Console.WriteLine("\n── 4. 热区落在图片范围内");
        {
            int inside = 0, tested = 0;
            foreach (var (cw, ch) in new[] { (900, 640), (1180, 860), (1920, 1080) })
            {
                var m = new DiagramMap(layout.ImageW, layout.ImageH, cw, ch);
                foreach (var it in layout.All)
                {
                    tested++;
                    var p = m.ToScreen(it.Dot[0], it.Dot[1]);
                    if (p.X >= 0 && p.X <= cw && p.Y >= 0 && p.Y <= ch) inside++;
                }
            }
            Check($"全部热区在控件内（{tested} 项 / 3 种尺寸）",
                  inside == tested && tested == layout.All.Count() * 3,
                  $"{inside}/{tested}");
        }

        // ─────────── 5. 命中判定（逐项，且真的走控件） ───────────
        Console.WriteLine("\n── 5. 命中判定（每个热区必须能命中自己）");
        {
            using var view = new DiagramView { Width = 1180, Height = 700 };
            view.Load(layout);

            Check("示意图已加载（资源真的复制过来了）",
                  view.DiagramImage is not null,
                  view.DiagramImage is null ? view.LoadError : $"{view.DiagramImage!.Width}×{view.DiagramImage.Height}");

            if (view.DiagramImage is not null)
            {
                Check("图片尺寸与 layout.json 一致",
                      view.DiagramImage.Width == layout.ImageW
                      && view.DiagramImage.Height == layout.ImageH,
                      $"{view.DiagramImage.Width}×{view.DiagramImage.Height} vs {layout.ImageW}×{layout.ImageH}");
            }

            int hitSelf = 0, miss = 0;
            var misses = new List<string>();
            foreach (var it in view.DrawOrder)
            {
                var c = view.CenterOf(it);
                var got = view.HitTest(Point.Round(c));
                if (ReferenceEquals(got, it)) hitSelf++;
                else { miss++; misses.Add($"{it.Id}->{got?.Id ?? "null"}"); }
            }
            Check($"每个热区中心命中自己（{view.DrawOrder.Count} 项）",
                  miss == 0 && hitSelf == layout.All.Count(),
                  miss == 0 ? "" : $"未命中 {miss}: {string.Join(", ", misses)}");

            // 防假绿：必须真的遍历到了全部热区
            Check("防假绿：确实遍历了全部 17 个热区",
                  view.DrawOrder.Count == layout.All.Count() && view.DrawOrder.Count >= 15,
                  $"实际 {view.DrawOrder.Count}");

            // 远离手柄的角落不应该命中任何热区
            Check("左上角空白处不命中任何热区",
                  view.HitTest(new Point(4, 4)) is null
                  || Math.Abs(view.HitTest(new Point(4, 4))!.Dot[0] - 0) < 0.2,
                  view.HitTest(new Point(4, 4))?.Id ?? "null");

            // 缩小到极小尺寸也不能崩、不能命中错乱
            view.Width = 120; view.Height = 80;
            bool crashed = false;
            try
            {
                var c = view.CenterOf(view.DrawOrder[0]);
                view.HitTest(Point.Round(c));
            }
            catch { crashed = true; }
            Check("极小控件尺寸下命中判定不抛异常", !crashed);
        }

        // ─────────── 6. 实际渲染不抛异常 ───────────
        Console.WriteLine("\n── 6. 实际绘制（多种尺寸）");
        {
            foreach (var (w, h) in new[] { (900, 500), (1180, 700), (1920, 1080), (200, 150) })
            {
                bool okDraw = true;
                string err = "";
                try
                {
                    using var view = new DiagramView { Width = w, Height = h };
                    view.Load(layout);
                    using var bmp = new Bitmap(w, h);
                    view.DrawToBitmap(bmp, new Rectangle(0, 0, w, h));
                }
                catch (Exception ex) { okDraw = false; err = ex.Message; }

                Check($"绘制 {w}×{h} 不抛异常", okDraw, err);
            }
        }

        // ─────────── 7. 槽位映射（0x0004 起是恒等） ───────────
        Console.WriteLine("\n── 7. 槽位映射 SlotMap");
        {
            Check("SlotCount 与 layout.json 的槽位数一致",
                  SlotMap.SlotCount == layout.Slots.Count,
                  $"{SlotMap.SlotCount} vs {layout.Slots.Count}");
            Check("SlotCount 与协议一致（26）", SlotMap.SlotCount == 26, $"{SlotMap.SlotCount}");

            var hl = new SlotMap.Highlights();

            // ★ 0x0005 起位号即槽位号 —— 逐位验证 26 个槽位，一个不多一个不少。
            //   比"抽查几个"强得多：一旦又引入什么映射转换，这里立刻红。
            int verified = 0;
            var mismatch = new List<string>();
            for (int bit = 0; bit < SlotMap.SlotCount; bit++)
            {
                SlotMap.Fill(Frame(0x03, 1u << bit), hl);
                if (hl.Valid && hl.ActiveCount == 1 && hl.IsSlotActive(bit)) verified++;
                else mismatch.Add($"{bit}->{{{string.Join(",", hl.Active)}}}");
            }
            Check("★ 26 个槽位逐位验证：bit(n) 只点亮槽位 n",
                  verified == SlotMap.SlotCount,
                  mismatch.Count == 0 ? "全部正确" : string.Join(" ", mismatch.Take(5)));
            Check("防假绿：确实遍历了全部 26 个槽位",
                  verified + mismatch.Count == SlotMap.SlotCount,
                  $"验证 {verified} + 不符 {mismatch.Count}");

            SlotMap.Fill(Frame(0x03, (1u << 3) | (1u << 10) | (1u << 23)), hl);
            Check("同时按槽位 3/10/23 → 三个都激活",
                  hl.ActiveCount == 3 && hl.IsSlotActive(3) && hl.IsSlotActive(10) && hl.IsSlotActive(23),
                  $"{{{string.Join(",", hl.Active.OrderBy(x => x))}}}");

            SlotMap.Fill(Frame(0x03, 0), hl);
            Check("没有按键 → 没有激活槽位", hl.ActiveCount == 0);

            // ★ 三组方向互不干扰（0x0003 只有一组，三个来源会互相串）
            SlotMap.Fill(Frame(0x03, 0, dirsDpad: 0x01, dirsL: 0x04, dirsR: 0x08), hl);
            Check("★ 三组方向互不串：十字键上 / 左摇杆左 / 右摇杆右",
                  hl.DirsOf("dpad") == SlotMap.DirUp
                  && hl.DirsOf("lstick") == SlotMap.DirLeft
                  && hl.DirsOf("rstick") == SlotMap.DirRight,
                  $"dpad={hl.DirsOf("dpad")} L={hl.DirsOf("lstick")} R={hl.DirsOf("rstick")}");

            // ★ 无效帧必须什么都不点亮 —— 否则设备断开的瞬间会残留高亮
            SlotMap.Fill(Frame(flags: 0x00, slots: 0xFFFFFF, dirsDpad: 0x0F, dirsL: 0x0F, dirsR: 0x0F), hl);
            Check("★ 无效帧（flags bit0=0）→ 不点亮任何槽位",
                  !hl.Valid && hl.ActiveCount == 0
                  && hl.DirsOf("dpad") == 0 && hl.DirsOf("lstick") == 0,
                  $"Valid={hl.Valid} 激活 {hl.ActiveCount}");

            // 从槽位位图反推方向（与固件 slots_to_dirs 同一约定）
            uint sl = (1u << 16) | (1u << 18);   // 左摇杆 ↑ 与 ←
            Check("DirsFromSlots 反推正确（槽位 16+18 → 上|左）",
                  SlotMap.DirsFromSlots(sl, SlotMap.BaseLstick) == (SlotMap.DirUp | SlotMap.DirLeft),
                  SlotMap.DirText(SlotMap.DirsFromSlots(sl, SlotMap.BaseLstick)));

            Check("DirText 拼装正确",
                  SlotMap.DirText(0) == "—"
                  && SlotMap.DirText(SlotMap.DirUp | SlotMap.DirRight) == "上右",
                  SlotMap.DirText(SlotMap.DirUp | SlotMap.DirRight));
        }

        // ─────────── 8. 高亮真的画出来了 ───────────
        Console.WriteLine("\n── 8. 实时高亮（渲染层）");
        {
            using var view = new DiagramView { Width = 1180, Height = 700 };
            view.Load(layout);

            var hl = new SlotMap.Highlights();
            // 按下槽位 A(0) 与 B(1)，左摇杆同时推到"上 + 左"
            SlotMap.Fill(Frame(0x03, (1u << 0) | (1u << 1), dirsL: 0x05), hl);
            view.SetHighlights(hl);

            // 单项断言：按下 A+B、方向 上+左
            bool okA = view.IsActive(layout.Items.First(i => i.Id == "a"));
            bool okB = view.IsActive(layout.Items.First(i => i.Id == "b"));
            bool okY = view.IsActive(layout.Items.First(i => i.Id == "y"));
            var lstick = layout.Items.First(i => i.Id == "lstick");
            var dpad = layout.Items.First(i => i.Id == "dpad");

            Check("按下 bit0/bit1 → 图上 A、B 两项为激活", okA && okB);
            Check("没按的 Y 不为激活（不能整片点亮）", !okY);
            Check("摇杆方向组有方向 → 左摇杆项为激活", view.IsActive(lstick));
            Check("旧固件没有十字键数据 → 十字键项不为激活（方向组不能串）",
                  !view.IsActive(dpad));

            // ★ 原来这里有"暂缓项永远不为激活"——现在 LT/RT 已经是**真实槽位**，
            //   暂缓项一个都没有了。改成验证它们确实是真项（这才是这次改造的目的），
            //   否则 .First() 会直接抛"Sequence contains no elements"把整个套件打断。
            Check("暂缓项已清空（LT/RT 不再是灰掉的装饰）", layout.Deferred.Count == 0,
                  $"剩 {layout.Deferred.Count} 个");
            var ltItem = layout.Items.First(i => i.Id == "lt");
            var rtItem = layout.Items.First(i => i.Id == "rt");
            Check("LT / RT 的槽位号是 24 / 25（追加在末尾，不动已有编号）",
                  ltItem.Slots.SequenceEqual(new[] { 24 })
                  && rtItem.Slots.SequenceEqual(new[] { 25 }),
                  $"LT={string.Join(",", ltItem.Slots)} RT={string.Join(",", rtItem.Slots)}");

            // ★ 最关键的一条：证明高亮**真的改了像素**。
            //   只断言"绘制不抛异常"是不够的 —— 画了但没画上去照样通过。
            var plain = RenderPng(view, 900, 520);
            view.SetHighlights(null);
            var noHl = RenderPng(view, 900, 520);
            view.SetHighlights(hl);
            var withHl = RenderPng(view, 900, 520);

            Check("高亮状态下的渲染结果与无高亮不同（确实画上去了）",
                  !withHl.SequenceEqual(noHl),
                  $"两次渲染各 {withHl.Length} / {noHl.Length} 字节");
            Check("同一个高亮状态渲染两次结果一致（渲染是确定的）",
                  plain.SequenceEqual(withHl));
            Check("防假绿：对比用的两张图都不是空的",
                  noHl.Length > 2000 && withHl.Length > 2000,
                  $"{noHl.Length} / {withHl.Length} 字节");

            // 清空高亮后必须回到无高亮的样子
            SlotMap.Fill(Frame(0x03, 0), hl);
            view.SetHighlights(hl);
            var cleared = RenderPng(view, 900, 520);
            Check("清空高亮后与初始无高亮渲染一致（没有残留）",
                  cleared.SequenceEqual(noHl));
        }

        // ─────────── 9. 槽位 ↔ 配置读写 ───────────
        Console.WriteLine("\n── 9. 槽位 ↔ 配置读写");
        {
            // 0x0004 起 24 个槽位全都是普通按键项 —— 不存在「不支持」的槽位了
            var supported = Enumerable.Range(0, SlotMap.SlotCount)
                                      .Where(SlotMap.SlotSupported).ToList();
            Check($"{SlotMap.SlotCount} 个槽位全部可读写（不再有「待升级」）",
                  supported.Count == SlotMap.SlotCount,
                  $"可读写 {supported.Count}/{SlotMap.SlotCount}");

            // 读写往返（含修饰键 —— 现在每个槽位都有 modifiers 字段）
            var cfg = KbConfig.CreateDefault();
            int rt = 0, rtBad = 0;
            for (int s = 0; s < SlotMap.SlotCount; s++)
            {
                SlotMap.WriteSlot(cfg, s, (byte)(0x04 + s), 0x02);
                var (c, m) = SlotMap.ReadSlot(cfg, s);
                rt++;
                if (c != (byte)(0x04 + s) || m != 0x02) rtBad++;
            }
            Check($"每个槽位写入后都能读回来（{rt} 个，含修饰键）",
                  rtBad == 0 && rt == SlotMap.SlotCount,
                  rtBad == 0 ? "" : $"{rtBad} 个不一致");

            // ★ 写一个槽位不能顺手改到别的槽位
            var cfg2 = KbConfig.CreateDefault();
            SlotMap.WriteSlot(cfg2, 0, 0x11, 0x01);
            bool othersIntact = true;
            var def0 = KbConfig.CreateDefault();
            for (int s = 1; s < SlotMap.SlotCount; s++)
                if (SlotMap.ReadSlot(cfg2, s) != SlotMap.ReadSlot(def0, s)) { othersIntact = false; break; }
            Check("★ 写槽位 0 不会连带改到别的槽位", othersIntact);

            // ★ 越界必须明确拒绝，且不改动任何字节
            var cfg3 = KbConfig.CreateDefault();
            var before = cfg3.ToBytes();
            // ★ 上界跟着槽位数走：24 以前是越界值，现在 24/25 是 LT/RT ——
            //   写死 24 的断言在扩到 26 槽位后就变成"错的"了。
            Check($"越界槽位（-1 / {SlotMap.SlotCount} / 99）被视为不支持",
                  !SlotMap.SlotSupported(-1) && !SlotMap.SlotSupported(SlotMap.SlotCount)
                  && !SlotMap.SlotSupported(99));
            Check($"边界槽位（0 / {SlotMap.SlotCount - 1}）是支持的",
                  SlotMap.SlotSupported(0) && SlotMap.SlotSupported(SlotMap.SlotCount - 1));
            bool wroteBad = SlotMap.WriteSlot(cfg3, -1, 0xAA, 0)
                         || SlotMap.WriteSlot(cfg3, 99, 0xAA, 0)
                         || SlotMap.WriteSlotEnabled(cfg3, 999, true);
            Check("★ 越界写入返回 false", !wroteBad);
            Check("★ 越界写入不能改动任何字节",
                  cfg3.ToBytes().SequenceEqual(before));

            // 启用开关：24 个槽位全都能禁启用
            Check("槽位 23（右摇杆→）也支持启用开关", SlotMap.SlotSupportsEnable(23));
            Check("禁用一个槽位后读回来是禁用", SlotMap.WriteSlotEnabled(cfg3, 5, false)
                  && !SlotMap.ReadSlotEnabled(cfg3, 5));

            // 宏绑定提示
            var cfg4 = KbConfig.CreateDefault();
            Check("没绑宏时没有提示", SlotMap.MacroNoteOf(cfg4, 0).Length == 0);
            cfg4.Buttons[7].UsesMacro = true;
            cfg4.Buttons[7].MacroId = 1;
            Check("绑了宏的槽位给出提示", SlotMap.MacroNoteOf(cfg4, 7).Contains("宏 2"),
                  SlotMap.MacroNoteOf(cfg4, 7));

            // 摇杆设置只挂在两个摇杆项上
            Check("摇杆设置挂在 lstick / rstick 上",
                  SlotMap.ItemHasStickSettings("lstick")
                  && SlotMap.ItemHasStickSettings("rstick")
                  && !SlotMap.ItemHasStickSettings("dpad"));
            Check("项 id → 摇杆下标正确",
                  SlotMap.StickIndexOfItem("lstick") == 0
                  && SlotMap.StickIndexOfItem("rstick") == 1
                  && SlotMap.StickIndexOfItem("a") == -1);
        }

        // ─────────── 10. 映射牌与弹面板 ───────────
        Console.WriteLine("\n── 10. 映射牌 + 映射编辑弹面板");
        {
            {
                // 键名显示：认不出来的键码也要显示出来，不能显示成"不映射"
                Check("(0,0) 显示为「不映射」",
                      ButtonLayoutPanel.ShortKeyText(0, 0) == "不映射");
                Check("认识的键码显示为名字（0x04 → A）",
                      ButtonLayoutPanel.ShortKeyText(0x04, 0) == "A",
                      ButtonLayoutPanel.ShortKeyText(0x04, 0));
                Check("★ 不认识的键码显示成 0xXX 而不是「不映射」",
                      ButtonLayoutPanel.ShortKeyText(0xE7, 0) == "0xE7",
                      ButtonLayoutPanel.ShortKeyText(0xE7, 0));

                using var view = new DiagramView { Width = 1180, Height = 700 };
                view.Load(layout);

                var cfg = KbConfig.CreateDefault();
                SlotMap.WriteSlot(cfg, 0, 0x04, 0);        // A
                SlotMap.WriteSlot(cfg, 1, 0x05, 0);        // B
                SlotMap.WriteSlot(cfg, 16, 0x52, 0);       // ↑
                SlotMap.WriteSlot(cfg, 17, 0x51, 0);       // ↓

                var text = new string[layout.Slots.Count];
                var edit = new bool[layout.Slots.Count];
                for (int s = 0; s < text.Length; s++)
                {
                    // 0x0004 起 24 个槽位全部可编辑，没有"待升级"这个状态了
                    edit[s] = SlotMap.SlotSupported(s);
                    text[s] = ButtonLayoutPanel.ShortKeyText(
                        SlotMap.ReadSlot(cfg, s).Code, SlotMap.ReadSlot(cfg, s).Mod);
                }
                view.SetSlotText(text, edit);

                var itemA = layout.Items.First(i => i.Id == "a");
                var itemL = layout.Items.First(i => i.Id == "lstick");
                Check("单键映射牌文字 = 当前映射（A）",
                      view.MappingTextOf(itemA) == "A", view.MappingTextOf(itemA));
                Check("方向类映射牌把 4 个方向都拼出来",
                      view.MappingTextOf(itemL).Contains("↑") && view.MappingTextOf(itemL).Contains("↓"),
                      view.MappingTextOf(itemL));
                Check("可编辑判定：槽位 0（A）可编辑", itemA.Slots.All(view.SlotEditable));
                // ★ 0x0004 起十字键也能编辑了 —— 0x0003 时固件只有 5 个槽位，
                //   这 4 个是"待升级"；现在 24 个槽位全部可编辑。
                Check("★ 可编辑判定：十字键（槽位 12..15）**也**可编辑",
                      view.AnySlotEditable(layout.Items.First(i => i.Id == "dpad")));
                Check("可编辑判定：左摇杆（槽位 16..19）可编辑", view.AnySlotEditable(itemL));
                Check("★ 全部 15 个交互项都可编辑（不再有「待升级」）",
                      layout.Items.All(view.AnySlotEditable),
                      string.Join(",", layout.Items.Where(i => !view.AnySlotEditable(i)).Select(i => i.Id)));

                // 牌子必须落在对应的原文字框里
                var lr = view.LabelRectOf(itemA);
                Check("映射牌矩形非空且尺寸合理",
                      !lr.IsEmpty && lr.Width > 20 && lr.Height > 5,
                      $"{lr.Width:F0}×{lr.Height:F0}");
                Check("映射牌在控件范围内",
                      lr.Left >= 0 && lr.Top >= 0 && lr.Right <= view.Width && lr.Bottom <= view.Height,
                      lr.ToString());
                Check("L3/R3 徽章没有原文字框（要靠徽章旁边的牌子）",
                      view.LabelRectOf(layout.Items.First(i => i.Id == "l3")).IsEmpty);

                // 点击 → 事件
                LayoutItem? clicked = null;
                view.HotspotActivated += (it, _) => clicked = it;
                var cA = view.CenterOf(itemA);
                view.GetType().GetMethod("OnMouseUp",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .Invoke(view, new object[] { new MouseEventArgs(MouseButtons.Left, 1,
                        (int)cA.X, (int)cA.Y, 0) });
                Check("点 A 热区 → 抛出 A 的事件", ReferenceEquals(clicked, itemA),
                      clicked?.Id ?? "null");

                clicked = null;
                view.GetType().GetMethod("OnMouseUp",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .Invoke(view, new object[] { new MouseEventArgs(MouseButtons.Right, 1,
                        (int)cA.X, (int)cA.Y, 0) });
                Check("右键不触发（避免误弹面板）", clicked is null);

                // 弹面板：单键 1 个下拉、方向类 4 个
                var cur1 = new List<(byte Code, byte Mod)> { (0x04, 0) };
                using (var p1 = SlotMapPopup.CreateForTest(Ctx(itemA, cur1, new List<bool> { true })))
                {
                    Check("单键弹面板建了 1 个下拉", p1.Combos.Count == 1, $"{p1.Combos.Count}");
                    Check("可编辑时下拉是启用的", p1.Combos[0].Enabled);
                }

                var cur4 = new List<(byte Code, byte Mod)> { (0x52, 0), (0x51, 0), (0x50, 0), (0x4F, 0) };
                using (var p4 = SlotMapPopup.CreateForTest(Ctx(itemL, cur4, new List<bool> { true, true, true, true })))
                {
                    Check("方向类弹面板建了 4 个下拉", p4.Combos.Count == 4, $"{p4.Combos.Count}");
                    Check("4 个下拉的当前值就是配置里的 4 个方向键",
                          p4.Combos[0].Text.Contains("Up") || p4.Combos[0].Text.Contains("上")
                          || p4.Combos[0].Text.Length > 0,
                          string.Join(" / ", p4.Combos.Select(c => c.Text)));
                }

                using (var p0 = SlotMapPopup.CreateForTest(Ctx(layout.Items.First(i => i.Id == "dpad"), cur4, new List<bool> { false, false, false, false })))
                {
                    Check("全部不可编辑时下拉全部禁用",
                          p0.Combos.Count == 4 && p0.Combos.All(c => !c.Enabled));
                }

                // 渲染不能抛 —— 而且**不能是一片空白**。
                // ★ 只断言"不抛异常"是假绿：第一版就是这样，截图一看整个弹面板
                //   是纯深色空框，因为 CreateControl() 之后子控件根本没布局。
                //   所以这里还要数一下渲染结果里有几种颜色。
                bool popupRenders = true;
                string perr = "";
                int colors = 0;
                try
                {
                    using var pp = SlotMapPopup.CreateForTest(Ctx(itemL, cur4, new List<bool> { true, true, true, true }));
                    pp.StartPosition = FormStartPosition.Manual;
                    pp.Location = new Point(-6000, -6000);
                    pp.ShowInTaskbar = false;
                    pp.Show();                      // ★ 必须 Show，否则子控件不布局
                    Application.DoEvents();
                    pp.PerformLayout();
                    Application.DoEvents();

                    using var b = new Bitmap(pp.Width, pp.Height);
                    pp.DrawToBitmap(b, new Rectangle(0, 0, b.Width, b.Height));

                    var seen = new HashSet<int>();
                    for (int y = 0; y < b.Height; y += 3)
                        for (int x = 0; x < b.Width; x += 3)
                            seen.Add(b.GetPixel(x, y).ToArgb());
                    colors = seen.Count;
                    pp.Close();
                }
                catch (Exception ex) { popupRenders = false; perr = ex.Message; }

                Check("弹面板能正常渲染", popupRenders, perr);
                Check("★ 弹面板渲染出的是真内容，不是空白框（颜色数 > 8）",
                      colors > 8, $"渲染出 {colors} 种颜色");
            }
        }

        // ─────────── 11. 布局校准（步骤 4） ───────────
        Console.WriteLine("\n── 11. 布局校准模式");
        {
            // 出厂坐标备份必须存在 —— 「恢复默认」全靠它
            Check("出厂坐标备份 layout.default.json 存在",
                  File.Exists(XboxLayout.DefaultBackupPath), XboxLayout.DefaultBackupPath);

            // 保存 / 读回 往返
            var tmp = Path.Combine(Path.GetTempPath(), $"kblayout_{Guid.NewGuid():N}.json");
            try
            {
                var lay = XboxLayout.Load(path);
                lay.Items.First(i => i.Id == "a").Dot = new[] { 0.1234, 0.5678 };
                lay.Save(tmp);

                Check("保存后没有留下 .tmp 临时文件", !File.Exists(tmp + ".tmp"));
                Check("保存出来的文件是合法 JSON 且能读回", File.Exists(tmp));

                var back = XboxLayout.Load(tmp);
                var a = back.Items.First(i => i.Id == "a");
                Check("坐标往返一致（0.1234, 0.5678）",
                      Math.Abs(a.Dot[0] - 0.1234) < 1e-9 && Math.Abs(a.Dot[1] - 0.5678) < 1e-9,
                      $"{a.Dot[0]},{a.Dot[1]}");
                Check("读回后自检仍然通过", back.Validate().Ok, back.Validate().Reason);

                // 中文不能写成 \uXXXX —— 能用但 git diff 里完全没法看
                var raw = File.ReadAllText(tmp, System.Text.Encoding.UTF8);
                Check("★ 中文没有被转义成 \\uXXXX（git diff 可读）",
                      raw.Contains("十字键") && !raw.Contains("\\u5341"),
                      raw.Contains("\\u5341") ? "被转义了" : "正常");

                // 快照 / 还原
                var snap = back.Snapshot();
                var a2 = back.Items.First(i => i.Id == "a");
                a2.Dot = new[] { 0.9, 0.9 };
                a2.LabelPx = new[] { 1, 2, 3, 4 };
                XboxLayout.Restore(snap);
                Check("快照还原把坐标恢复回去了",
                      Math.Abs(a2.Dot[0] - 0.1234) < 1e-9
                      && a2.LabelPx.Length == 4 && a2.LabelPx[0] != 1,
                      $"{a2.Dot[0]},{a2.LabelPx[0]}");

                // 恢复出厂
                var lay3 = XboxLayout.Load(path);
                var dpad = lay3.Items.First(i => i.Id == "dpad");
                var dpad0 = (double[])dpad.Dot.Clone();
                dpad.Dot = new[] { 0.01, 0.99 };
                Check("恢复出厂返回成功", lay3.ResetToFactoryDefaults(out var why), why);
                Check("恢复出厂把坐标还原到备份里的值",
                      Math.Abs(dpad.Dot[0] - dpad0[0]) < 1e-9
                      && Math.Abs(dpad.Dot[1] - dpad0[1]) < 1e-9,
                      $"{dpad.Dot[0]},{dpad.Dot[1]} vs {dpad0[0]},{dpad0[1]}");
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }

            // ── 校准模式下的命中与拖动 ──
            using var view = new DiagramView { Width = 1180, Height = 700 };
            view.Load(XboxLayout.Load(path));
            var itemA = view.DrawOrder.First(i => i.Id == "a");

            var cA = view.CenterOf(itemA);
            var dh = view.DotHandleOf(itemA);
            Check("坐标点手柄以热区点为中心",
                  Math.Abs(dh.X + dh.Width / 2 - cA.X) < 0.01f
                  && Math.Abs(dh.Y + dh.Height / 2 - cA.Y) < 0.01f);

            var lr = view.LabelRectOf(itemA);
            Check("标签框抓取范围比标签框本身大（细框点不中）",
                  view.LabelGrabOf(itemA).Width > lr.Width,
                  $"{view.LabelGrabOf(itemA).Width:F0} vs {lr.Width:F0}");

            // 非校准模式下点击 = 弹映射面板；校准模式下不能弹
            int activated = 0;
            view.HotspotActivated += (_, _) => activated++;
            view.SimulateClick(Point.Round(cA));
            Check("非校准模式：点热区弹出映射面板", activated == 1, $"{activated} 次");

            view.CalibrationMode = true;
            view.SimulateClick(Point.Round(cA));
            Check("★ 校准模式：点热区**不**弹映射面板（否则拖一下就弹窗）",
                  activated == 1, $"共 {activated} 次");

            int edits = 0;
            view.LayoutEdited += () => edits++;

            var before = (double[])itemA.Dot.Clone();
            view.SimulateDrag(Point.Round(cA), new Point((int)cA.X + 40, (int)cA.Y + 25));
            Check("拖动坐标点改变了归一化坐标", Math.Abs(itemA.Dot[0] - before[0]) > 0.005,
                  $"{before[0]:F4} -> {itemA.Dot[0]:F4}");
            Check("拖动过程中触发了 LayoutEdited（调用方据此标记未保存）", edits > 0, $"{edits} 次");
            Check("拖动后坐标仍在 0..1 内",
                  itemA.Dot[0] >= 0 && itemA.Dot[0] <= 1 && itemA.Dot[1] >= 0 && itemA.Dot[1] <= 1);

            // 拖到界外必须被夹住，不能产生越界坐标
            var dh2 = view.DotHandleOf(itemA);
            view.SimulateDrag(new Point((int)dh2.X, (int)dh2.Y), new Point(-500, -500));
            Check("★ 拖到控件外坐标被夹在 0..1（不会写出越界值）",
                  itemA.Dot[0] >= 0 && itemA.Dot[0] <= 1
                  && itemA.Dot[1] >= 0 && itemA.Dot[1] <= 1,
                  $"{itemA.Dot[0]:F4},{itemA.Dot[1]:F4}");

            // 拖标签框
            var lbl0 = (int[])itemA.LabelPx.Clone();
            var lg = view.LabelGrabOf(itemA);
            view.SimulateDrag(new Point((int)(lg.Left + 4), (int)(lg.Top + 4)),
                              new Point((int)(lg.Left + 44), (int)(lg.Top + 24)));
            Check("拖动标签框改变了 LabelPx", itemA.LabelPx[0] != lbl0[0],
                  $"{lbl0[0]} -> {itemA.LabelPx[0]}");
            Check("标签框拖动保持宽高（整体平移）",
                  itemA.LabelPx[2] - itemA.LabelPx[0] == lbl0[2] - lbl0[0]
                  && itemA.LabelPx[3] - itemA.LabelPx[1] == lbl0[3] - lbl0[1]);

            // 校准层必须真的画出来
            var plain = RenderPng(view, 900, 520);
            view.CalibrationMode = false;
            var noCalib = RenderPng(view, 900, 520);
            Check("★ 校准层确实画上去了（两次渲染不同）", !plain.SequenceEqual(noCalib));
        }

        // ─────────── 12. 读数条的显示逻辑（接线核对着它） ───────────
        Console.WriteLine("\n── 12. 读数条显示（接线核对要看的）");
        {
            // 造一帧：左摇杆正常（x=3000 中心 2048），右摇杆报故障
            var p = new byte[InputState.PayloadSize];
            p[0] = 0x03;
            void PutU16(int off, ushort v) { p[off] = (byte)(v & 0xFF); p[off + 1] = (byte)(v >> 8); }
            // ★ 偏移跟着 0x0005 走：槽位段 3→4 字节，所以 raw 从 8→9、center 16→17、
            //   travel 36→38、axis_fault 32→33。手搓载荷最容易漏改的就是这里。
            PutU16(9, 3000);  PutU16(11, 2048);     // 左 X / Y 原始值
            PutU16(13, 20);   PutU16(15, 20);       // 右 X / Y（悬空时读到贴轨值）
            for (int a = 0; a < 4; a++)
            {
                PutU16(17 + a * 2, 2048);
                PutU16(38 + a * 4, 0);
                PutU16(40 + a * 4, 4095);
            }
            p[33] = 0x0C;                            // bit2|bit3 = 右摇杆两轴故障
            var st = InputState.Parse(p);

            Check("正常的轴显示原始值 + 百分比",
                  ButtonLayoutPanel.FormatAxis(st, InputState.AxisLX).Contains("3000"),
                  ButtonLayoutPanel.FormatAxis(st, InputState.AxisLX));
            Check("★ 故障轴显示「未接线/已禁用」而不是数字（否则会去调一个没接的轴）",
                  ButtonLayoutPanel.FormatAxis(st, InputState.AxisRX).Contains("未接线")
                  && !ButtonLayoutPanel.FormatAxis(st, InputState.AxisRX).Contains("20"),
                  ButtonLayoutPanel.FormatAxis(st, InputState.AxisRX));

            // ★ 读数条的「按键」列必须包含方向槽位 —— 正在焊十字键时靠它核对
            var hl = new SlotMap.Highlights();
            hl.Clear(); hl.Valid = true;
            hl.Active.Add(SlotMap.BaseDpad + 0);        // 十字键↑
            hl.Active.Add(SlotMap.BaseLstick + 2);      // 左摇杆←
            hl.Active.Add(1);                            // B
            var txt = ButtonLayoutPanel.ActiveSlotText(hl);
            Check("★ 「按键」列包含方向槽位（十字键 / 摇杆），不只是单键",
                  txt.Contains("十字键 ↑") && txt.Contains("左摇杆 ←") && txt.Contains("B 键"),
                  txt);

            hl.Clear(); hl.Valid = true;
            Check("没有按下时显示「—」", ButtonLayoutPanel.ActiveSlotText(hl) == "—");
        }

        // ─────────── 13. 重绘开销（手柄一动 UI 就卡的那件事） ───────────
        Console.WriteLine("\n── 13. 重绘开销与「内容没变不重绘」");
        {
            // ★ 数的是**请求重绘**的次数，不是 OnPaint 次数 —— 原因见
            //   DiagramView.RepaintRequestCountForTest 的注释（未显示/屏幕外的控件
            //   都不会产生 WM_PAINT，三条路都试过）。
            using var view = new DiagramView { Width = 1180, Height = 700 };
            view.Load(layout);

            var hl = new SlotMap.Highlights();
            hl.Valid = true;

            // 先确认计数路径真的通了（否则下面所有计数都是空的 —— 假绿就是这么来的）
            hl.Active.Clear(); hl.Active.Add(0);
            view.SetHighlights(hl);
            Check("防假绿：首次 SetHighlights 确实请求了重绘",
                  view.RepaintRequestCountForTest > 0,
                  $"请求次数 {view.RepaintRequestCountForTest}");
            int afterFirst = view.RepaintRequestCountForTest;

            // 同样的内容再来 50 次 —— 一次都不该请求重绘
            for (int i = 0; i < 50; i++) view.SetHighlights(hl);
            Check("★ 高亮内容没变时完全不重绘（50 次调用 0 次请求）",
                  view.RepaintRequestCountForTest == afterFirst,
                  $"请求次数 {afterFirst} → {view.RepaintRequestCountForTest}");

            // 变一下就必须重绘
            hl.Active.Add(1);
            view.SetHighlights(hl);
            Check("高亮内容变了就重绘", view.RepaintRequestCountForTest > afterFirst,
                  $"请求次数 {view.RepaintRequestCountForTest}");
            // ★ 缓存的收益：同一轮绘制里，"复用静态层"应当明显快于"每次重建"。
            //   绝对耗时随机器变，所以用**比值**断言，不用绝对阈值。
            const int N = 12;
            byte[] Render(bool forceRebuild)
            {
                if (forceRebuild) view.ForceStaticLayerRebuildForTest();
                return RenderPng(view, 1180, 700);
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < N; i++) Render(false);
            double fastMs = sw.Elapsed.TotalMilliseconds / N;

            sw.Restart();
            for (int i = 0; i < N; i++) Render(true);
            double slowMs = sw.Elapsed.TotalMilliseconds / N;

            Check($"★ 复用静态层明显快于每次重建（{fastMs:F1}ms vs {slowMs:F1}ms 每次）",
                  fastMs * 2 < slowMs,
                  $"加速 {slowMs / Math.Max(0.01, fastMs):F1}×");

            Check("复用静态层时单次重绘在合理范围内（< 40ms）",
                  fastMs < 40, $"{fastMs:F1}ms");

            Console.WriteLine($"       （参考：接 20Hz 上报，单次重绘预算 50ms；"
                            + $"重建一次静态层约 {slowMs:F1}ms）");

            // ── ★ 遍历**每一个**槽位：单独按下它必须请求一次重绘 ──
            //   这条就是为"扳机按下界面不高亮"那个 bug 加的。
            //   原来只测了槽位 0/1，而签名函数里写死了 `< 24`，
            //   槽位 24/25 被静默忽略 —— 只按扳机时签名不变、一次重绘都不发。
            //   阴的地方在于：同时按了别的键它又会亮，属于"有时候好有时候不好"。
            using var viewEach = new DiagramView { Width = 1180, Height = 700 };
            viewEach.Load(layout);

            // ★ 基线固定成"全灭（但数据有效）"，每个槽位都拿它当参照。
            //   不能"和上一次调用比" —— 那样槽位 23→24 因为签名从 bit23 变成 0
            //   也**算**变化，24 会侥幸通过、只有 25 报红（负向验证时就是这样）。
            var baseline = new SlotMap.Highlights();
            baseline.Clear(); baseline.Valid = true;
            viewEach.SetHighlights(baseline);

            var silent = new List<string>();
            for (int s = 0; s < SlotMap.SlotCount; s++)
            {
                var one = new SlotMap.Highlights();
                one.Clear(); one.Valid = true;
                one.Active.Add(s);

                int beforeEach = viewEach.RepaintRequestCountForTest;
                viewEach.SetHighlights(one);
                if (viewEach.RepaintRequestCountForTest == beforeEach)
                    silent.Add($"{s}({SlotMap.NameOf(s)})");

                viewEach.SetHighlights(baseline);   // 回到基线，让下一次参照一致
            }
            Check($"★ {SlotMap.SlotCount} 个槽位逐个按下都会触发重绘（没有槽位被静默忽略）",
                  silent.Count == 0,
                  silent.Count == 0 ? "全部有效" : "被忽略：" + string.Join(" ", silent));

            // ── LT/RT 的圈确实是"可高亮"的那一种 ──
            //   ★ 注意这条**不能**用来防"不重绘"那个 bug：
            //     RenderPng 走 DrawToBitmap，那是**强制**渲染，
            //     不管有没有请求重绘都会画一遍 —— 把 bug 改回去它照样通过。
            //     （和"内容没变不重绘"那条踩的是同一个坑，负向验证时发现的。）
            //   它真正证明的是：LT/RT 不再是改造前那种"暂缓项"的灰色样式，
            //     而是按实时状态上色的普通热区；防"不重绘"的职责在上面那条
            //     遍历签名测试里。
            using var viewLt = new DiagramView { Width = 1180, Height = 700 };
            viewLt.Load(layout);
            foreach (var id in new[] { "lt", "rt" })
            {
                var it = layout.Items.First(i => i.Id == id);

                // ★ 两边都必须 Valid=true —— 唯一差别只能剩"那个槽位比特"。
                //   原来 off 用 Valid=false、on 用 Valid=true，光 Valid 位就足以
                //   触发重绘，把 bug 改回去这条照样通过（假绿）。
                var off = new SlotMap.Highlights();
                off.Clear(); off.Valid = true;
                viewLt.SetHighlights(off);
                var offPng = RenderPng(viewLt, 1180, 700);

                var on = new SlotMap.Highlights();
                on.Clear(); on.Valid = true;
                on.Active.Add(it.Slots[0]);
                viewLt.SetHighlights(on);
                var lit = RenderPng(viewLt, 1180, 700);

                Check($"{it.Name} 的圈按实时状态上色（不再是灰色暂缓样式）",
                      !offPng.SequenceEqual(lit),
                      $"{offPng.Length} 字节渲染 {(offPng.SequenceEqual(lit) ? "完全相同" : "有差异")}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(new string('═', 60));
        Console.WriteLine($"通过 {pass} / 失败 {fail}");
        return fail == 0 ? 0 : 1;
    }

    // ══════════════════════════════════════════════════════════════
    //  辅助
    // ══════════════════════════════════════════════════════════════

    /// <summary>造一个弹面板上下文（测试用；摇杆项默认带上死区/反向那两块）</summary>
    private static PopupContext Ctx(LayoutItem item,
                                    List<(byte Code, byte Mod)> cur,
                                    List<bool> editable,
                                    bool stickSettings = false)
        => new()
        {
            Item = item,
            Current = cur,
            Editable = editable,
            Enabled = Enumerable.Repeat(true, cur.Count).ToList(),
            Notes = Enumerable.Repeat("", cur.Count).ToList(),
            ShowStickSettings = stickSettings || (item.IsDir4 && item.Id == "lstick"),
            DeadzonePct = 12,
        };

    /// <summary>造一帧 52 字节的 Input Report（布局见 InputState 注释 / app_config.h 的 IN_OFF_*）</summary>
    private static InputState Frame(byte flags, uint slots, byte dirsDpad = 0,
                                    byte dirsL = 0, byte dirsR = 0, uint seq = 1)
    {
        var p = new byte[InputState.PayloadSize];
        p[0] = flags;
        // ★ 偏移跟着 0x0005 走：槽位 3→4 字节，后面所有段整体后移 1
        //   （dirs 从 4 到 5、seq 从 28 到 29）。写死数字的代价就是这里要跟着改。
        p[1] = (byte)(slots & 0xFF);
        p[2] = (byte)((slots >> 8) & 0xFF);
        p[3] = (byte)((slots >> 16) & 0xFF);
        p[4] = (byte)((slots >> 24) & 0xFF);
        p[5] = dirsDpad;
        p[6] = dirsL;
        p[7] = dirsR;
        p[29] = (byte)(seq & 0xFF);
        p[30] = (byte)((seq >> 8) & 0xFF);
        p[31] = (byte)((seq >> 16) & 0xFF);
        p[32] = (byte)((seq >> 24) & 0xFF);
        return InputState.Parse(p);
    }

    /// <summary>把控件渲染成 PNG 字节，用于逐字节比较两次渲染是否不同</summary>
    private static byte[] RenderPng(Control c, int w, int h)
    {
        using var bmp = new Bitmap(w, h);
        c.DrawToBitmap(bmp, new Rectangle(0, 0, w, h));
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return ms.ToArray();
    }
}
