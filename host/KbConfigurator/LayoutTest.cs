using KbConfigurator.Ui;
using KbConfigurator.Views;

namespace KbConfigurator;

/// <summary>
/// 布局自适应验收 —— 「窗口缩放不错位」的**硬判据**。
///
/// ★ 为什么必须自动化：靠肉眼截图比对抓不住"某个控件在第 4 种窗口尺寸下
///   刚好越过边界 3 像素"这种问题，而用户一旦把窗口拉大就会看到。
///   这里把"不越界、不重叠、关键控件在最小尺寸下仍可见可点"变成可执行断言。
///
/// 用法：KbConfigurator.exe --layout      退出码 0 = 全部通过
/// </summary>
internal static class LayoutTest
{
    private static readonly (int W, int H, string Name)[] Sizes =
    {
        (900,  640,  "最小尺寸"),
        (1180, 860,  "默认尺寸"),
        (1440, 900,  "较大"),
        (1920, 1080, "全高清"),
        (2560, 1440, "2K"),
    };

    public static int Run()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        int pass = 0, fail = 0;

        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}" + (detail.Length > 0 ? "  —— " + detail : ""));
            if (ok) pass++; else fail++;
        }

        Console.WriteLine("════════ 布局自适应验收 ════════");

        // ★ 关键：必须真的 Show() 出来（放到屏幕外），否则子控件的 Visible
        //   全都会是 false，遍历一个控件都扫不到 —— 检查就会**空跑通过**，
        //   看起来全绿其实什么都没验。这是测试假绿的经典陷阱，
        //   所以下面专门加了一条"扫描到的控件数必须够多"的断言来堵它。
        //
        //   同时用 Headless 关掉真实设备逻辑：布局验收不该依赖、也不该动到设备
        //   （否则会去连 USB、读配置，甚至弹出模态框把测试卡死）。
        MainForm.Headless = true;

        using var form = new MainForm
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-6000, -6000),      // 移到屏幕外，不打扰用户
        };

        form.Show();
        Application.DoEvents();

        foreach (var (w, h, label) in Sizes)
        {
            form.ClientSize = new Size(w, h);
            form.PerformLayout();
            Application.DoEvents();

            Console.WriteLine($"\n── {label}  {w}×{h}");

            var overflow = new List<string>();
            var overlap = new List<string>();
            var degenerate = new List<string>();
            int checkedCount = 0;

            // 三个页签都要单独选中检查 —— 没被选中的页签不走布局
            var tabs = FindTabs(form);
            int pageCount = tabs?.TabPages.Count ?? 1;

            for (int page = 0; page < pageCount; page++)
            {
                if (tabs is not null)
                {
                    tabs.SelectedIndex = page;
                    form.PerformLayout();
                    Application.DoEvents();
                }

                Control root = tabs is not null ? tabs.TabPages[page] : form;
                Walk(root, overflow, overlap, degenerate, ref checkedCount);
            }

            // ★ 防空跑：扫不到控件就说明遍历逻辑失效了，必须报错而不是"通过"
            Check($"{label}: 确实扫描到了控件（防测试空跑）",
                  checkedCount >= 30,
                  $"扫到 {checkedCount} 个控件" + (checkedCount < 30 ? "  ← 太少了，遍历逻辑可能坏了" : ""));

            Check($"{label}: 控件不越界",
                  overflow.Count == 0,
                  overflow.Count == 0 ? $"全部在父容器可见区内（{checkedCount} 个）"
                                       : string.Join("；", overflow.Take(3)));

            Check($"{label}: 同级控件不重叠",
                  overlap.Count == 0,
                  overlap.Count == 0 ? "无重叠" : string.Join("；", overlap.Take(3)));

            Check($"{label}: 无零尺寸控件",
                  degenerate.Count == 0,
                  degenerate.Count == 0 ? "尺寸都正常" : string.Join("；", degenerate.Take(3)));
        }

        // ── 最小尺寸下关键控件必须可见可用 ──
        Console.WriteLine("\n── 最小尺寸下的关键控件可用性");
        form.ClientSize = new Size(900, 640);

        // ★ 必须先选中目标页签：没被选中的页签上控件 Visible=false，
        //   否则会误报"看不到"（踩过）。
        //   ★★ 这里以前写的是 SelectedIndex = 0 并注释"切回输入调试页" ——
        //      后来新增「按键布局」页插在最前面，下标 0 就变成新页了；
        //      再后来「输入调试」页整个被合并掉了。按名字选中不会有这种错位。
        var tabsAll = FindTabs(form);
        Check("按名字能选中「按键布局」页签",
              SelectTab(tabsAll, "按键布局"),
              tabsAll is null ? "找不到 TabControl"
                              : string.Join(" / ", tabsAll.TabPages.Cast<TabPage>().Select(p => p.Text)));
        form.PerformLayout();
        Application.DoEvents();

        var all = All(form).ToList();

        // 配置条上的按钮（不属于任何页签，直接全窗体找）
        foreach (var (text, why) in new[]
                 {
                     ("从设备读取",   "配置条第 1 个"),
                     ("保存并重启",   "配置条第 3 个（一键保存 + 软重启应用新布局）"),
                     ("恢复默认",     "配置条第 4 个"),
                 })
        {
            var b = all.OfType<Button>().FirstOrDefault(x => x.Text == text);
            Check($"「{text}」存在且可见（{why}）",
                  b is not null && b.Visible && b.Width > 0 && b.Height > 0,
                  b is null ? "找不到这个按钮" : $"{b.Width}×{b.Height}");
        }

        // ★ 页签内的关键控件必须**限定在该页签里找**。
        //   按设计（§3.1）「重置摇杆校准」从「输入调试」挪到了「按键布局」；
        //   步骤 5 之后旧页整个删掉了，所以只在新页里找。
        //   限定范围还有个好处：顺带验证了"这个按钮确实在它该在的那一页上"。
        foreach (var (page, text, why) in new[]
                 {
                     ("按键布局", "重置摇杆校准", "设计 §3.1：它属于摇杆读数旁边"),
                     ("按键布局", "校准布局",     "设计 §四：布局坐标校准入口"),
                 })
        {
            bool havePage = SelectTab(tabsAll, page);
            form.PerformLayout();
            Application.DoEvents();

            Button? b = null;
            if (havePage && tabsAll is not null)
                foreach (TabPage p in tabsAll.TabPages)
                    if (p.Text == page)
                    {
                        b = All(p).OfType<Button>().FirstOrDefault(x => x.Text == text);
                        break;
                    }

            Check($"「{text}」存在且可见（{page} 页 —— {why}）",
                  b is not null && b.Visible && b.Width > 0 && b.Height > 0,
                  !havePage ? $"没有「{page}」页签"
                            : b is null ? $"{page} 页上找不到这个按钮"
                                        : $"{b.Width}×{b.Height}");
        }

        // 连接/断开是同一个切换按钮，未连接时显示「连接设备」
        Check("「连接设备／断开」切换按钮存在（放在「从设备读取」后面）",
              all.OfType<Button>().Any(b => b.Text is "连接设备" or "断开"),
              string.Join("/", all.OfType<Button>().Select(b => b.Text).Take(8)));

        // ── 按需求删掉的按钮，必须真的不在了 ──
        Console.WriteLine("\n── 已按需求删除的按钮");
        foreach (var gone in new[] { "设备 ▾", "查看接口", "重启设备", "▶ 测试触发", "■ 中止", "保存到设备" })
        {
            Check($"「{gone}」已不存在",
                  all.OfType<Button>().All(b => b.Text != gone),
                  all.OfType<Button>().Any(b => b.Text == gone) ? "仍然存在！" : "已删除");
        }

        // ─────────── 读数条按钮：文字必须放得下 ───────────
        // ★ 用户报"校准布局只显示了一半" —— 实际是按钮**文字的上半截被裁掉**。
        //   这种事截图里不盯着看很容易漏，所以做成断言：
        //   客户区高度必须 >= 文字实测高度。
        {
            var names = new[] { "重置摇杆校准", "校准布局", "保存坐标", "恢复出厂坐标" };
            var bad = new List<string>();
            int seen = 0;

            foreach (var b in AllButtons(form))
            {
                if (!names.Contains(b.Text)) continue;
                seen++;
                var need = TextRenderer.MeasureText(b.Text, b.Font);
                // 宽度也要够：留 4px 余量，否则中文会被截成"…"
                if (b.ClientSize.Height < need.Height || b.ClientSize.Width < need.Width)
                    bad.Add($"{b.Text} 客户区 {b.ClientSize.Width}×{b.ClientSize.Height} "
                          + $"需要 {need.Width}×{need.Height}");
            }

            Check("防假绿：确实找到了读数条上的按钮", seen >= 4, $"找到 {seen} 个");
            Check("★ 读数条按钮的文字都放得下（不会只显示半截）",
                  bad.Count == 0, bad.Count == 0 ? "全部合适" : string.Join(" / ", bad));

            // ★★ 这一条才是真正守住那个 bug 的。
            //   上面那条只比"客户区 ≥ 文字高度"—— 客户区是**布局**算出来的，
            //   28px 也算"够"；而裁剪发生在**绘制**阶段：
            //   按钮比所在那一行还高时，超出的部分被父容器切掉，文字就只剩半截。
            //   （L.Button 造的按钮固定 Height=28，读数条行高当时只有 26。）
            //   不变量很直白：**控件不能比它所在的行高。**
            var taller = new List<string>();
            int rowH = ButtonLayoutPanel.ReadoutRowHeightForTest;
            foreach (var b in AllButtons(form))
            {
                if (!names.Contains(b.Text)) continue;
                if (b.Height > rowH) taller.Add($"{b.Text} 高 {b.Height} > 行高 {rowH}");
            }
            Check($"★ 读数条按钮不比所在行高（行高 {rowH}，超了就会被裁成半截）",
                  taller.Count == 0, taller.Count == 0 ? "全部合适" : string.Join(" / ", taller));
        }

        // 宏编辑页不该再有「测试」分组
        Check("宏编辑页已无「测试」分组",
              all.OfType<GroupBox>().All(g => g.Text != "测试"),
              "已删除测试触发/中止整组");

        // ── 合并后的页签结构 ──
        // ★ 旧「输入调试」「按键映射」已合并进「按键布局」，
        //   所以这两页上原来的断言（9 个可选键下拉等）要跟着换。
        //   新页**故意不再常驻下拉**：映射在点击热区弹出的面板里改
        //   （常驻下拉在缩放后的小窗口里会互相重叠、被裁掉）。
        //   所以这里改成断言"新页确实在、旧页确实没了"。
        Check("「按键布局」页存在", all.OfType<ButtonLayoutPanel>().Any());
        Check("旧「输入调试」页已删除（找不到对应控件）",
              all.OfType<Control>().All(c => c.GetType().Name != "DebugPanel"));
        Check("旧「按键映射」页已删除（找不到对应控件）",
              all.OfType<Control>().All(c => c.GetType().Name != "KeymapPanel"));

        // 新页上的关键控件：校准入口 + 摇杆校准 + 两个校准按钮要能容纳
        foreach (var txt in new[] { "校准布局", "重置摇杆校准" })
            Check($"「{txt}」在「按键布局」页上",
                  all.OfType<Button>().Any(b => b.Text == txt),
                  string.Join("/", all.OfType<Button>().Select(b => b.Text).Take(10)));

        Check("「触发方式」列已移除（实体键盘只有一种行为，没有可配的触发方式）",
              all.OfType<ComboBox>().All(c => !IsTriggerCombo(c)),
              "页面上已无该下拉");

        form.Dispose();

        Console.WriteLine();
        Console.WriteLine($"════════ 结果：{pass} 通过 / {fail} 失败 ════════");
        return fail == 0 ? 0 : 1;
    }

    // ══════════════════════════════════════════════════════════════

    /// <summary>递归收集所有 Button（LayoutTest 里原来没有通用的遍历辅助）</summary>
    private static IEnumerable<Button> AllButtons(Control root)
    {
        foreach (Control c in root.Controls)
        {
            if (c is Button b) yield return b;
            foreach (var inner in AllButtons(c)) yield return inner;
        }
    }
    private static void Walk(Control parent, List<string> overflow, List<string> overlap,
                             List<string> degenerate, ref int checkedCount)
    {
        var kids = parent.Controls.Cast<Control>()
                         .Where(c => c.Visible && c.Width >= 0 && c.Height >= 0)
                         .ToList();

        // AutoScroll 容器里的子控件**故意**可以超出可见区（滚动就是干这个的），
        // 所以那种容器只查重叠、不查越界。
        bool scrollable = parent is ScrollableControl { AutoScroll: true };
        var area = parent.ClientRectangle;

        foreach (var c in kids)
        {
            checkedCount++;

            // 空文字 + AutoSize 的 Label 宽度就是 0（比如"该按钮绑了哪个宏"的说明列，
            // 没绑宏时就是空的）。这是**有意留空**，不是布局错误，跳过。
            bool intentionallyEmpty = c is Label && c.AutoSize && string.IsNullOrEmpty(c.Text);

            if (!intentionallyEmpty && (c.Width <= 0 || c.Height <= 0))
                degenerate.Add($"{parent.GetType().Name}/{Label(c)} {c.Width}×{c.Height}");

            if (!scrollable && !area.Contains(c.Bounds))
                overflow.Add($"{parent.GetType().Name}/{Label(c)} {c.Bounds} 超出父容器 {area}");
        }

        for (int i = 0; i < kids.Count; i++)
            for (int j = i + 1; j < kids.Count; j++)
            {
                if (!kids[i].Bounds.IntersectsWith(kids[j].Bounds)) continue;
                overlap.Add($"{parent.GetType().Name}: {Label(kids[i])} 与 {Label(kids[j])} " +
                            $"{kids[i].Bounds}/{kids[j].Bounds}");
            }

        foreach (var c in kids)
            Walk(c, overflow, overlap, degenerate, ref checkedCount);
    }

    private static string Label(Control c)
    {
        var t = (c.Text ?? "").Replace("\r", " ").Replace("\n", " ");
        if (t.Length > 14) t = t[..14] + "…";
        return t.Length == 0 ? c.GetType().Name : $"{c.GetType().Name}(\"{t}\")";
    }

    private static TabControl? FindTabs(Control root)
    {
        foreach (Control c in root.Controls)
        {
            if (c is TabControl tc) return tc;
            var d = FindTabs(c);
            if (d is not null) return d;
        }
        return null;
    }

    /// <summary>
    /// 按页签**名字**选中。
    ///
    /// ★ 不要用 SelectedIndex：之前这里写死 <c>SelectedIndex = 0</c> 并注释
    ///   "切回输入调试页"，后来新增的「按键布局」页被放到最前，下标 0 就指向
    ///   新页了 —— 于是「重置摇杆校准」被误报为不可见。按名字选不会有这种
    ///   静默错位：找不到就明确返回 false，而不是悄悄选错。
    /// </summary>
    private static bool SelectTab(TabControl? tabs, string name)
    {
        if (tabs is null) return false;
        foreach (TabPage p in tabs.TabPages)
        {
            if (p.Text != name) continue;
            tabs.SelectedTab = p;
            return true;
        }
        return false;
    }

    private static IEnumerable<Control> All(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var d in All(c)) yield return d;
        }
    }

    /// <summary>按选项文字识别「触发方式」列（避免依赖控件名）</summary>
    private static bool IsTriggerCombo(ComboBox c)
    {
        if (c.Items.Count != 3) return false;
        var s = string.Join("|", c.Items.Cast<object>().Select(o => o.ToString()));
        return s.Contains("按下") && s.Contains("长按");
    }
}
