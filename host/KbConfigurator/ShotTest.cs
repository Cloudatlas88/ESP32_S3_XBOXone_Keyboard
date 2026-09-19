using KbConfigurator.Protocol;
using KbConfigurator.Views;

namespace KbConfigurator;

/// <summary>
/// 把主窗体渲染成 PNG —— 用法：KbConfigurator.exe --shot [输出路径] [页签名]
///
/// ★ 为什么需要它：布局验收只能证明"不越界、不重叠、能命中"，
///   证不了"看起来对不对"。热区是不是压在按键上、名字有没有盖住图，
///   只有真的看一眼才知道。有了这个模式，UI 每次改动都能留一张图对比，
///   也能直接发给用户确认，不用让对方自己去点。
/// </summary>
internal static class ShotTest
{
    public static int Run(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var outPath = args.Length > 1
            ? args[1]
            : Path.Combine(AppContext.BaseDirectory, "shot.png");
        var pageName = args.Length > 2 ? args[2] : "按键布局";
        var w = args.Length > 3 && int.TryParse(args[3], out var pw) ? pw : 1280;
        var h = args.Length > 4 && int.TryParse(args[4], out var ph) ? ph : 820;

        // 默认 Headless（不碰设备）——布局/外观截图不该依赖设备在不在。
        // ★ 但传了 "live" 就连真设备：读数条上那些数字（摇杆原始值、按键名、
        //   原始电平）只有连着设备才有内容，否则截图里全是"—"，等于没验
        //   —— 真实的数字格式只能这样看，断言只能验证"包含某个子串"。
        bool live = args.Contains("live");
        MainForm.Headless = !live;

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var form = new MainForm
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-6000, -6000),
            ClientSize = new Size(w, h),
        };
        form.Show();
        Application.DoEvents();

        // 按名字选页签：不能按下标，下标会随页签顺序变动
        var tabs = FindTabs(form);
        if (tabs is null) { Console.Error.WriteLine("找不到页签控件"); return 1; }

        TabPage? target = null;
        foreach (TabPage p in tabs.TabPages)
            if (p.Text == pageName) { target = p; break; }

        if (target is null)
        {
            Console.Error.WriteLine($"没有「{pageName}」页签。现有：" +
                string.Join(" / ", tabs.TabPages.Cast<TabPage>().Select(p => p.Text)));
            return 1;
        }

        tabs.SelectedTab = target;
        form.PerformLayout();
        Application.DoEvents();

        // 截图要把**所有**热区的名字画出来（平时只在悬停时显示一个，否则糊成一片），
        // 并把指定的项画成悬停态 —— 这样一张图就能同时核对"圈的位置对不对"
        // 和"悬停长什么样"，省得让用户自己去晃鼠标。
        var panel = All(target).OfType<ButtonLayoutPanel>().FirstOrDefault();
        if (panel is not null)
        {
            // 映射牌上已经有名称了，热区旁边再画一遍名字会互相压
            panel.Diagram.ShowAllNames = false;
            panel.Diagram.SetHoverById(args.Length > 5 ? args[5] : null);
            if (args.Length > 6 && !args[6].Equals("calib", StringComparison.OrdinalIgnoreCase))
                InjectHighlights(panel, args[6]);
            if (args.Contains("calib")) panel.SetCalibrationMode(true);
            form.PerformLayout();
            Application.DoEvents();
        }

        // ★ live 模式要让消息泵多跑一会儿再截：
        //   设备是**异步**连上的 —— 构造后 PollDevice 才去枚举/连接/读配置，
        //   截图太快会拍到"已连上但还没收到第一帧"的中间态，
        //   读数条全是「—」、帧率还是上一句说明文字。
        //   （第一次就是这么拍到的：顶栏握手正常、图上 RB 都亮了，读数条却没数。）
        if (live)
        {
            var settle = System.Diagnostics.Stopwatch.StartNew();
            while (settle.ElapsedMilliseconds < 2500)
            {
                Application.DoEvents();
                Thread.Sleep(20);
            }
        }

        using var bmp = new Bitmap(w, h);
        form.DrawToBitmap(bmp, new Rectangle(0, 0, w, h));
        bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);

        Console.WriteLine($"✔ 已保存 {outPath}  ({w}×{h}, 页签「{pageName}»)");

        // 顺带把这一页的热区清单打出来，方便和图对照
        if (panel?.LayoutData is { } lay)
        {
            // 再渲染一张映射弹面板 —— 它是新加的可见界面，
            // 而"看不看得见"是布局验收证明不了的（这一页已经栽过两次）。
            if (args.Contains("popup"))
            {
                foreach (var id in new[] { "lstick", "a" })
                {
                    var pit = lay.Items.FirstOrDefault(x => x.Id == id);
                    if (pit is null) continue;

                    var cur = Enumerable.Range(0, Math.Max(1, pit.Slots.Count))
                                        .Select(_ => ((byte)0x52, (byte)0)).ToList();
                    var ed = Enumerable.Repeat(true, cur.Count).ToList();

                    var ctx = new Views.PopupContext
                    {
                        Item = pit,
                        Current = cur,
                        Editable = ed,
                        Enabled = Enumerable.Repeat(true, cur.Count).ToList(),
                        Notes = Enumerable.Repeat("", cur.Count).ToList(),
                        ShowStickSettings = pit.IsDir4 && pit.Id == "lstick",
                        DeadzonePct = 12,
                    };

                    using var pop = Views.SlotMapPopup.CreateForTest(ctx);
                    // ★ 必须真的 Show()（挪到屏幕外）再截图。
                    //   只 CreateControl() 的话子控件没有真正布局，
                    //   DrawToBitmap 画出来是一片空白 —— 这个坑 LayoutTest 里记过一次。
                    pop.StartPosition = FormStartPosition.Manual;
                    pop.Location = new Point(-6000, -6000);
                    pop.ShowInTaskbar = false;
                    pop.Show();
                    Application.DoEvents();
                    pop.PerformLayout();
                    Application.DoEvents();

                    using var pb = new Bitmap(pop.Width, pop.Height);
                    pop.DrawToBitmap(pb, new Rectangle(0, 0, pb.Width, pb.Height));
                    var pp = Path.Combine(Path.GetDirectoryName(outPath) ?? ".", $"popup_{id}.png");
                    pb.Save(pp, System.Drawing.Imaging.ImageFormat.Png);
                    Console.WriteLine($"  -> 映射弹面板 {id}: {pp} ({pb.Width}×{pb.Height})");

                    // ★★ 还要截**展开的下拉列表**。
                    //   用户报的就是"下拉框都是黑的" —— 而 DrawToBitmap 只画窗体自己，
                    //   展开的列表是**另一个顶层窗口**，根本不在里面，
                    //   所以上面那张图看不出这个问题（收起状态本来就是正常的）。
                    //   这里把窗体真的显示在屏幕上、展开下拉，再用 CopyFromScreen 截屏。
                    if (args.Contains("dropdown"))
                    {
                        pop.Location = new Point(80, 80);
                        Application.DoEvents();
                        var combo = FindCombo(pop);
                        if (combo is null)
                        {
                            Console.WriteLine("  !! 弹面板里找不到下拉框");
                        }
                        else
                        {
                            combo.DroppedDown = true;
                            Application.DoEvents();
                            Thread.Sleep(400);          // 等列表窗口画出来
                            Application.DoEvents();

                            // 列表在组合框下方展开，整块截下来
                            var scr = combo.PointToScreen(new Point(0, 0));
                            int listH = combo.Height + Math.Min(240, combo.Items.Count * 16 + 8);
                            var rect = new Rectangle(scr.X, scr.Y, combo.Width, listH);
                            using var db = new Bitmap(rect.Width, rect.Height);
                            using (var g = Graphics.FromImage(db))
                                g.CopyFromScreen(rect.Location, Point.Empty, rect.Size);

                            var dp = Path.Combine(Path.GetDirectoryName(outPath) ?? ".",
                                                  $"dropdown_{id}.png");
                            db.Save(dp, System.Drawing.Imaging.ImageFormat.Png);

                            int colors = CountColors(db);
                            Console.WriteLine($"  -> 展开的下拉列表 {id}: {dp} "
                                            + $"({db.Width}×{db.Height}, {colors} 种颜色)");
                            // 全黑 = 没人画（DrawItem 没挂上）；正常应该有很多种颜色
                            Console.WriteLine($"     判定：{(colors > 8 ? "✔ 有内容" : "✗ 一片黑，DrawItem 没生效")}");

                            combo.DroppedDown = false;
                            Application.DoEvents();
                        }
                    }

                    pop.Close();
                }
            }
            // ★ 传 "calib" 就先进校准模式再截图。
            //   用户报的是"校准布局只显示了一半" —— 那是**进入校准模式之后**的画面，
            //   普通截图根本拍不到。看不到就只能靠猜，所以给截图工具加个开关，
            //   先把现象拍下来再谈修。
            if (args.Contains("calib"))
            {
                panel.SetCalibrationMode(true);
                Application.DoEvents();

                // 校准模式下坐标牌只在"悬停项"上显示，所以还得模拟一次悬停 ——
                // 否则拍出来的是一张干净的图，什么问题都看不出来。
                var firstReal = panel.Diagram.DrawOrder.FirstOrDefault(x => !x.IsBadge);
                if (firstReal is not null)
                {
                    Console.WriteLine($"  （已进入校准模式，DrawOrder 第一项 = {firstReal.Name}）");
                }
                else
                {
                    Console.WriteLine("  （已进入校准模式）");
                }
            }

            Console.WriteLine($"  布局：{panel.LoadNote}");
            Console.WriteLine($"  图：{(panel.Diagram.DiagramImage is { } img
                ? $"{img.Width}×{img.Height}"
                : "未加载 — " + panel.Diagram.LoadError)}");
            Console.WriteLine();
            Console.WriteLine("  热区清单（屏幕坐标基于图片显示区）:");
            foreach (var it in panel.Diagram.DrawOrder)
            {
                var c = panel.Diagram.CenterOf(it);
                Console.WriteLine($"    {it.Id,-8} {it.Name,-16} 槽位[{string.Join(",", it.Slots),-12}]"
                                + $" 屏幕({c.X,7:F1},{c.Y,6:F1})");
            }
        }
        else
        {
            Console.WriteLine($"  （「{pageName}」页上没有 ButtonLayoutPanel）");
        }

        return 0;
    }

    /// <summary>
    /// 按槽位号注入一组假的高亮，用于截图展示"按下时是什么样"。
    /// 参数形如 <c>"0,1,16,18"</c>：列出要激活的槽位号。
    /// 顺带把列出的摇杆方向槽位（16..19 / 20..23）翻译成方向掩码，
    /// 这样箭头也会亮起来。
    /// </summary>
    private static void InjectHighlights(ButtonLayoutPanel panel, string spec)
    {
        var hl = panel.Highlights;
        hl.Clear();
        hl.Valid = true;

        foreach (var tok in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(tok.Trim(), out var slot)) continue;
            hl.Active.Add(slot);

            // 槽位 16..19 = 左摇杆 上/下/左/右，20..23 = 右摇杆
            if (slot >= 16 && slot <= 23)
            {
                string group = slot < 20 ? "lstick" : "rstick";
                int bit = (slot - (slot < 20 ? 16 : 20)) switch
                {
                    0 => SlotMap.DirUp,
                    1 => SlotMap.DirDown,
                    2 => SlotMap.DirLeft,
                    _ => SlotMap.DirRight,
                };
                hl.DirBits[group] = hl.DirsOf(group) | bit;
            }
        }

        panel.Diagram.SetHighlights(hl);
    }

    private static TabControl? FindTabs(Control root)    {
        foreach (Control c in root.Controls)
        {
            if (c is TabControl tc) return tc;
            var d = FindTabs(c);
            if (d is not null) return d;
        }
        return null;
    }

    private static IEnumerable<Control> All(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var d in All(c)) yield return d;
        }
    }
    /// <summary>在控件树里找第一个 ComboBox</summary>
    private static ComboBox? FindCombo(Control root)
    {
        foreach (Control c in root.Controls)
        {
            if (c is ComboBox cb) return cb;
            if (FindCombo(c) is { } inner) return inner;
        }
        return null;
    }

    /// <summary>数一张位图里出现过多少种颜色（用来判断「是不是一片黑」）</summary>
    private static int CountColors(Bitmap b)
    {
        var seen = new HashSet<int>();
        for (int y = 0; y < b.Height; y += 2)
            for (int x = 0; x < b.Width; x += 2)
                seen.Add(b.GetPixel(x, y).ToArgb());
        return seen.Count;
    }
}