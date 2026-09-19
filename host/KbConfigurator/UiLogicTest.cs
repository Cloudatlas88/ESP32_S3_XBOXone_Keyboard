using KbConfigurator.Model;
using KbConfigurator.Protocol;
using KbConfigurator.Views;

namespace KbConfigurator;

/// <summary>
/// 界面**交互逻辑**验收（不开窗口，直接驱动真实控件）。
///
/// ★ 为什么需要它：UI 重构把宏步骤编辑器整个写成了新逻辑
///   （宏列表 / 加一步 / 删一步 / 上移下移 / 显式步数记数），
///   但当时只验证了「读 → 存字节完全一致」——
///   那只覆盖了"载入后原样写回"，**增删排序这些新操作一次都没跑过**。
///   而它们恰恰是最容易写错的（删中间一步要整体前移、移完要重排按钮可用性）。
///
///   所以这里**直接点真实按钮**（PerformClick），走的是和用户点击完全相同的那套处理，
///   再读回模型断言结果 —— 而不是绕过界面去调内部方法，
///   那样测不出"按钮没接上事件"这类问题。
///
/// 用法：KbConfigurator.exe --uilogic      退出码 0 = 全部通过
/// </summary>
internal static class UiLogicTest
{
    public static int Run()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        int pass = 0, fail = 0;

        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}" + (detail.Length > 0 ? "  —— " + detail : ""));
            if (ok) pass++; else fail++;
        }

        Console.WriteLine("════════ 界面交互逻辑验收 ════════");

        MacroStepTests(Check);
        KeySearchTests(Check);
        TriggerSemanticsTests(Check);

        Console.WriteLine();
        Console.WriteLine($"════════ 结果：{pass} 通过 / {fail} 失败 ════════");
        return fail == 0 ? 0 : 1;
    }

    // ══════════════════════════════════════════════════════════════
    //  宏步骤增删排序
    // ══════════════════════════════════════════════════════════════

    /// <summary>在控件树里找第一个指定类型的后代</summary>
    private static T? FindDescendant<T>(Control root) where T : Control
    {
        foreach (Control c in root.Controls)
        {
            if (c is T hit) return hit;
            if (FindDescendant<T>(c) is { } inner) return inner;
        }
        return null;
    }

    /// <summary>发窗口消息（用来模拟真实滚轮 —— 见 MacroStepTests 里的说明）</summary>
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private static void MacroStepTests(Action<string, bool, string> Check)
    {
        Console.WriteLine("\n── 宏步骤：加一步 / 删一步 / 上移下移（点真实按钮）");

        var cfg = BuildMacroCfg();
        using var panel = new MacroPanel();
        panel.LoadFromModel(cfg);

        // 先确认载入正确
        panel.WriteToModel(cfg);
        Check("载入 4 步的宏", Steps(cfg) == "TAP04|RAND200/30|TAP05|RAND300/60", Steps(cfg));

        // 诊断 + 防回归：触发绑定下拉框里的按键，必须和「最终稿」的槽位表一致。
        // 用户报的就是这里列的东西不对，所以先把实际内容打出来，别凭猜。
        {
            var tb = panel.TriggerBindingForTest;
            var listed = tb.Items.Cast<object>().Select(o => o.ToString()).ToList();
            Console.WriteLine($"       触发绑定列了 {listed.Count} 项：{string.Join(" / ", listed)}");
            Check($"触发绑定项数 = 槽位表 {SlotMap.SlotCount} 项 + 1 个「不触发」",
                  listed.Count == SlotMap.SlotCount + 1,
                  $"{listed.Count} 项");
            var expect = new List<string> { "（不触发）" };
            expect.AddRange(SlotMap.SlotNames);
            Check("★ 触发绑定的按键名与「最终稿」槽位表逐项一致",
                  listed.SequenceEqual(expect),
                  listed.SequenceEqual(expect) ? "一致"
                      : "第一处不同：" + string.Join(" | ", listed.Zip(expect)
                          .Where(p => p.First != p.Second).Take(3)
                          .Select(p => $"{p.First}≠{p.Second}")));
        }
        var ops = FindStepOps(panel);
        Check("界面上能找到 4 行的操作按钮（每行 ↑↓✕）", ops.Count >= 4,
              $"找到 {ops.Count} 行");

        if (ops.Count < 4) return;

        // ── 删掉第 2 步（RAND200/30）──
        ops[1].del.PerformClick();
        panel.WriteToModel(cfg);
        Check("★ 删掉第 2 步后：后面两步整体前移",
              Steps(cfg) == "TAP04|TAP05|RAND300/60",
              Steps(cfg));

        // ── 把第 1 步下移 ──
        ops = FindStepOps(panel);
        ops[0].down.PerformClick();
        panel.WriteToModel(cfg);
        Check("★ 第 1 步下移：与前一步交换",
              Steps(cfg) == "TAP05|TAP04|RAND300/60",
              Steps(cfg));

        // ── 再把它上移回来 ──
        ops = FindStepOps(panel);
        ops[1].up.PerformClick();
        panel.WriteToModel(cfg);
        Check("★ 上移还原：顺序恢复",
              Steps(cfg) == "TAP04|TAP05|RAND300/60",
              Steps(cfg));

        // ── 加一步 ──
        var add = FindAddButton(panel);
        Check("界面上有「加入步骤」按钮", add is not null, add?.Text ?? "找不到");

        if (add is not null)
        {
            add.PerformClick();
            panel.WriteToModel(cfg);
            Check("★ 加入步骤后步数变成 4（前三步不变）",
                  cfg.Macros[0].StepCount == 4 &&
                  Steps(cfg).StartsWith("TAP04|TAP05|RAND300/60"),
                  $"步数={cfg.Macros[0].StepCount} 内容={Steps(cfg)}");
        }

        // ── 首行不能上移、末行不能下移 ──
        ops = FindStepOps(panel);
        int n = cfg.Macros[0].StepCount;
        Check("首行「上移」被禁用", !ops[0].up.Enabled, "");
        Check("末行「下移」被禁用", !ops[n - 1].down.Enabled, $"共 {n} 步");
        Check("中间行的上移/下移都可用",
              ops.Count < 2 || (ops[1].up.Enabled && ops[1].down.Enabled), "");

        // ── 一路删空 ──
        for (int guard = 0; guard < 12; guard++)
        {
            ops = FindStepOps(panel);
            if (cfg.Macros[0].StepCount == 0) break;
            ops[0].del.PerformClick();
            panel.WriteToModel(cfg);
        }
        Check("★ 连续删除能把宏清空（步数归 0，不会删成负数）",
              cfg.Macros[0].StepCount == 0, $"步数={cfg.Macros[0].StepCount}");

        // ── 加到上限后不能再加 ──
        // ★ 上限从常量取，别写死 8 —— 这次从 8 提到 20 就是被写死的数字绊了一次。
        var add2 = FindAddButton(panel);
        for (int i = 0; i < KbConfig.MacroStepMax + 4; i++) add2?.PerformClick();
        panel.WriteToModel(cfg);
        Check($"★ 步数上限 {KbConfig.MacroStepMax}，超出后不再增加",
              cfg.Macros[0].StepCount == KbConfig.MacroStepMax,
              $"步数={cfg.Macros[0].StepCount}（上限 {KbConfig.MacroStepMax}）");

        // ── ★ 加满 20 步后，每一行都要真的有控件（用户报的"第 9 步不显示"）──
        var opsFull = FindStepOps(panel);
        Check($"★ 加满 {KbConfig.MacroStepMax} 步后每一行都在界面上（不再只显示前 8 行）",
              opsFull.Count >= KbConfig.MacroStepMax,
              $"界面上有 {opsFull.Count} 行，期望 ≥ {KbConfig.MacroStepMax}");
        Check("★ 加满后末行「下移」被禁用、第 1 行「上移」被禁用（行确实是按真实步数排的）",
              opsFull.Count > 0 && !opsFull[0].up.Enabled
              && !opsFull[KbConfig.MacroStepMax - 1].down.Enabled,
              $"共 {opsFull.Count} 行");

        // ── ★ 步骤列表必须可滚动，且装满时内容高于可视区 ──
        var scroller = panel.StepScrollForTest;
        Check("步骤列表放在可滚动容器里", scroller is not null && scroller.AutoScroll,
              scroller is null ? "找不到滚动容器" : $"AutoScroll={scroller.AutoScroll}");

        if (scroller is not null && scroller.Controls.Count > 0)
        {
            int content = scroller.Controls[0].Height;
            Check($"★ 装满 {KbConfig.MacroStepMax} 步时内容高于可视区（所以**必须**能滚）",
                  content > scroller.ClientSize.Height,
                  $"内容 {content}px > 可视 {scroller.ClientSize.Height}px");
            Check("★ 可视区高度确实小于全部步骤摊开的高度（没有把整页撑爆）",
                  scroller.Height < KbConfig.MacroStepMax * 34,
                  $"可视 {scroller.Height}px < 全部 {KbConfig.MacroStepMax * 34}px");

            // ── 滚轮真的能翻页 ──
            //   ★ 用 SendMessage 往窗口句柄发真实的 WM_MOUSEWHEEL，
            //     而不是直接调 WndProc（protected，而且绕过消息泵，
            //     测不出"消息真的送得到"）。
            //   ★ delta **正 = 向上滚**（在顶部自然不动），负 = 向下滚。
            //     第一版把方向写反了，白白红了两条。
            const int WM_MOUSEWHEEL = 0x020A;
            IntPtr Wheel(int delta) => unchecked((IntPtr)(long)(delta << 16));

            scroller.AutoScrollPosition = new Point(0, 0);
            Check("起始在顶部", -scroller.AutoScrollPosition.Y == 0,
                  $"位置 {-scroller.AutoScrollPosition.Y}");

            SendMessage(scroller.Handle, WM_MOUSEWHEEL, Wheel(-120), IntPtr.Zero);
            int down = -scroller.AutoScrollPosition.Y;
            Check("★ 向下滚，列表跟着滚", down > 0, $"滚动位置 0 → {down}");

            SendMessage(scroller.Handle, WM_MOUSEWHEEL, Wheel(120), IntPtr.Zero);
            int up = -scroller.AutoScrollPosition.Y;
            Check("★ 向上滚能回滚（不是只能往下）", up < down, $"滚动位置 {down} → {up}");

            // ── ★★ 这一条才是这次真正加的东西：鼠标停在**步骤行上**滚轮也要管用 ──
            //   ComboBox / NumericUpDown 默认会吃掉 WM_MOUSEWHEEL
            //   （拿它改选中项/改数值），不转发的话用户把鼠标放在行上滚毫无反应。
            var wheelCombo = FindDescendant<ComboBox>(scroller);
            if (wheelCombo is null)
            {
                Check("步骤行里有下拉框可供滚轮转发测试", false, "找不到 ComboBox");
            }
            else
            {
                scroller.AutoScrollPosition = new Point(0, 0);
                SendMessage(wheelCombo.Handle, WM_MOUSEWHEEL, Wheel(-120), IntPtr.Zero);
                int viaCombo = -scroller.AutoScrollPosition.Y;
                Check("★★ 鼠标停在步骤行的下拉框上，滚轮照样翻页（没有被下拉框吃掉）",
                      viaCombo > 0, $"经下拉框滚动 0 → {viaCombo}");

                var wheelNum = FindDescendant<NumericUpDown>(scroller);
                if (wheelNum is not null)
                {
                    scroller.AutoScrollPosition = new Point(0, 0);
                    SendMessage(wheelNum.Handle, WM_MOUSEWHEEL, Wheel(-120), IntPtr.Zero);
                    int viaNum = -scroller.AutoScrollPosition.Y;
                    Check("★★ 鼠标停在数值框上，滚轮照样翻页",
                          viaNum > 0, $"经数值框滚动 0 → {viaNum}");
                }
            }
        }
        else
        {
            Check("步骤列表里有内容控件", false, "滚动容器是空的");
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  按键搜索
    // ══════════════════════════════════════════════════════════════

    private static void KeySearchTests(Action<string, bool, string> Check)
    {
        Console.WriteLine("\n── 按键表本身是否合法（防参数写反）");

        // ★ 这条抓到过一个真实的老 Bug：组合键那几项把 (Code, Modifier) 传反了，
        //   导致「Ctrl+A」实际发出【键码 0x01 + 左Alt】这种垃圾。
        //   位置参数字段类型都相同，编译器查不出来，只能靠断言。
        var problems = KeyCodes.Validate();
        Check($"键表 {KeyCodes.All.Count} 条全部格式合法", problems.Count == 0,
              problems.Count == 0 ? "键码在 0x04~0xE7、修饰键位合法" : string.Join("；", problems.Take(4)));

        // 逐个核对组合键的键码（写反的话这里是第一道防线）
        foreach (var (name, code, mod) in new[]
                 {
                     ("Ctrl+A", (byte)0x04, KeyCodes.MOD_LCTRL),
                     ("Ctrl+C", (byte)0x06, KeyCodes.MOD_LCTRL),
                     ("Ctrl+V", (byte)0x19, KeyCodes.MOD_LCTRL),
                     ("Ctrl+X", (byte)0x1B, KeyCodes.MOD_LCTRL),
                     ("Ctrl+Z", (byte)0x1D, KeyCodes.MOD_LCTRL),
                     ("Ctrl+S", (byte)0x16, KeyCodes.MOD_LCTRL),
                     ("Alt+Tab", (byte)0x2B, KeyCodes.MOD_LALT),
                 })
        {
            var k = KeyCodes.All.FirstOrDefault(x => x.Name == name);
            Check($"「{name}」键码/修饰键正确",
                  k is not null && k.Code == code && k.Modifier == mod,
                  k is null ? "表里没有" : $"键码=0x{k.Code:X2}(应 0x{code:X2}) mod=0x{k.Modifier:X2}(应 0x{mod:X2})");
        }

        Console.WriteLine("\n── 按键搜索（原来 110 项平铺，找 PageDown 要翻半天）");

        int total = KeyCodes.All.Count;

        Check("空关键字返回全部", KeyCodes.Search("").Count == total, $"{total} 项");

        // ★ 每条搜索都要求"结果集确实被缩小了"。
        //   早先只断言"结果里有 PageDown"，而搜索失败会退回全部 ——
        //   全量里当然也有 PageDown，于是**假通过**了（实测踩过）。
        void Narrows(string q, string expectName, string why)
        {
            var hits = KeyCodes.Search(q).ToList();
            bool narrowed = hits.Count < total;
            bool found = hits.Any(h => h.Name.Contains(expectName));
            Check($"搜「{q}」能筛出 {expectName}（{why}）",
                  narrowed && found,
                  $"{(narrowed ? $"{hits.Count} 项" : $"⚠ 没有缩小，仍是全部 {hits.Count} 项 —— 过滤没生效")}" +
                  $"：{string.Join("/", hits.Take(4).Select(h => h.Name))}");
        }

        Narrows("pgdn",      "PageDown", "缩写");
        Narrows("pagedown",  "PageDown", "全拼");
        Narrows("page down", "PageDown", "带空格");
        Narrows("PGDN",      "PageDown", "大写");
        Narrows("esc",       "Esc",      "缩写");
        Narrows("del",       "Delete",   "缩写");

        // ★ 箭头符号必须真的当方向键搜 —— 早先箭头被当标点剥掉，
        //   查询变成空串退回全部，看起来"搜到了"其实没过滤。
        Narrows("↓",    "Down", "箭头符号");
        Narrows("下",   "Down", "中文");
        Narrows("down", "Down", "英文");

        var ctrl = KeyCodes.Search("ctrl").Select(k => k.Name).ToList();
        Check("搜「ctrl」能筛出 Ctrl+? 组合且确实缩小了",
              ctrl.Count is > 0 && ctrl.Count < total && ctrl.All(n => n.Contains("Ctrl")),
              $"{ctrl.Count} 项：{string.Join("/", ctrl)}");

        // ★ 敲错字不能变成空下拉 —— 否则用户会以为"按键列表坏了"
        Check("搜不存在的字符时退回全部（不会变成空列表）",
              KeyCodes.Search("zzqqxx").Count == total,
              $"{KeyCodes.Search("zzqqxx").Count} 项");
    }

    // ══════════════════════════════════════════════════════════════
    //  触发语义：与实体键盘一致
    // ══════════════════════════════════════════════════════════════

    private static void TriggerSemanticsTests(Action<string, bool, string> Check)
    {
        Console.WriteLine("\n── 触发语义：和实体键盘一致（没有「触发方式」可配）");

        var cfg = KbConfig.CreateDefault();
        // 故意在配置里塞旧版遗留的「释放触发 / 长按触发」值
        cfg.Buttons[0].Trigger = 1;
        cfg.Buttons[1].Trigger = 2;

        using var panel = new ButtonLayoutPanel();

        // ★ trigger 的固化现在在**会话层**（ConfigSession.Normalize），
        //   不在某个页签的 WriteToModel 里 —— 以前是旧「按键映射」页顺手做的，
        //   合并页签时这种"藏在某一页里"的行为最容易一起丢掉。
        //   所以这里必须走真实的 ConfigSession 路径来验，而不是直接调面板。
        var session = new ConfigSession();
        session.Register(panel);
        session.Replace(cfg);

        // 实体键盘只有一种逻辑（按下=保持、松开=抬起），固件已不读 trigger 字段，
        // 所以配置里必须把它固化成 0 —— 留个会被忽略的值只会让人误解。
        Check("★ 读入配置时就把废弃的 trigger 字段固化为 0（旧配置里的 1/2 不会留下）",
              session.Config.Buttons.All(b => b.Trigger == 0),
              "按钮 trigger=" + string.Join(",", session.Config.Buttons.Select(b => b.Trigger)));

        // 再塞一遍再走 CollectFromUi，确认保存路径上也会固化
        session.Config.Buttons[0].Trigger = 2;
        session.CollectFromUi();
        Check("★ 保存路径（CollectFromUi）也会固化 trigger",
              session.Config.Buttons.All(b => b.Trigger == 0),
              "按钮 trigger=" + string.Join(",", session.Config.Buttons.Select(b => b.Trigger)));

        // 界面上不该再有"触发方式"下拉
        var trig = Descendants(panel).OfType<ComboBox>().FirstOrDefault(c =>
            c.Items.Count == 3 && string.Join("|", c.Items.Cast<object>()).Contains("长按"));
        Check("★ 界面上已无「触发方式」下拉（这个概念不存在了）",
              trig is null,
              trig is null ? "已移除" : "仍然存在：" + string.Join("/", trig.Items.Cast<object>()));

        // 但按键键码/修饰键必须照常写回
        var cfg2 = KbConfig.CreateDefault();
        cfg2.Buttons[2].Keycode = 0x04;
        cfg2.Buttons[2].Modifiers = KeyCodes.MOD_LCTRL;
        panel.LoadFromModel(cfg2);
        panel.WriteToModel(cfg2);
        Check("组合键（Ctrl+A）仍照常写回",
              cfg2.Buttons[2].Keycode == 0x04 && cfg2.Buttons[2].Modifiers == KeyCodes.MOD_LCTRL,
              $"键码=0x{cfg2.Buttons[2].Keycode:X2} mod=0x{cfg2.Buttons[2].Modifiers:X2}");

        // ── ★ 表示不了的组合【不能被静默归零】 ──
        //    Ctrl+Space（0x2C + LCTRL）不在键表里。早先的实现会退回"不映射"，
        //    用户一点保存就把原值抹掉 —— 和"保存一次宏就清零"是同一类数据损失。
        //    新页的做法是保留**原始 (键码, 修饰键) 对**，只在用户真的在弹面板里
        //    改了才更新，所以这条数据损失的路径根本不存在。
        var cfg3 = KbConfig.CreateDefault();
        cfg3.Buttons[3].Keycode = 0x2C;
        cfg3.Buttons[3].Modifiers = KeyCodes.MOD_LCTRL;
        panel.LoadFromModel(cfg3);
        panel.WriteToModel(cfg3);
        Check("★ 表示不了的组合（Ctrl+Space）不会被静默清零",
              cfg3.Buttons[3].Keycode == 0x2C && cfg3.Buttons[3].Modifiers == KeyCodes.MOD_LCTRL,
              $"键码=0x{cfg3.Buttons[3].Keycode:X2} mod=0x{cfg3.Buttons[3].Modifiers:X2}（原 0x2C + LCTRL）");

        var cfg4 = KbConfig.CreateDefault();
        cfg4.Buttons[4].Keycode = 0xE7;      // 键表里根本没有这个键码
        cfg4.Buttons[4].Modifiers = 0x40;
        panel.LoadFromModel(cfg4);
        panel.WriteToModel(cfg4);
        Check("★ 完全不认识的键码（0xE7）也能保住",
              cfg4.Buttons[4].Keycode == 0xE7 && cfg4.Buttons[4].Modifiers == 0x40,
              $"键码=0x{cfg4.Buttons[4].Keycode:X2} mod=0x{cfg4.Buttons[4].Modifiers:X2}（原 0xE7/0x40）");

        // 死区 / 反向这两项原来在旧「按键映射」页，合并后要能在新页写回
        var cfg5 = KbConfig.CreateDefault();
        cfg5.Sticks[0].DeadzonePct = 20; cfg5.Sticks[0].InvertX = 1; cfg5.Sticks[0].InvertY = 0;
        panel.LoadFromModel(cfg5);
        cfg5.Sticks[0].DeadzonePct = 0; cfg5.Sticks[0].InvertX = 0;      // 先改坏
        panel.WriteToModel(cfg5);
        Check("摇杆死区 / X、Y 反向能照常写回（原来在旧「按键映射」页）",
              cfg5.Sticks[0].DeadzonePct == 20 && cfg5.Sticks[0].InvertX == 1 && cfg5.Sticks[0].InvertY == 0,
              $"死区={cfg5.Sticks[0].DeadzonePct}% InvX={cfg5.Sticks[0].InvertX} InvY={cfg5.Sticks[0].InvertY}");
    }

    // ══════════════════════════════════════════════════════════════
    //  工具
    // ══════════════════════════════════════════════════════════════

    /// <summary>造一个 4 步的宏：TAP A → 随机延迟(200,30%) → TAP B → 随机延迟(300,60%)</summary>
    private static KbConfig BuildMacroCfg()
    {
        var cfg = KbConfig.CreateDefault();
        cfg.MacroCount = 1;

        var m = cfg.Macros[0];
        m.StepCount = 4;
        m.TimeoutMs = 10000;
        m.Steps[0] = new KbConfig.Step { Action = KbConfig.ActKeyTap, Keycode = 0x04 };
        m.Steps[1] = new KbConfig.Step { Action = KbConfig.ActRandDelay, DelayMinMs = 200, DelayMaxMs = 30 };
        m.Steps[2] = new KbConfig.Step { Action = KbConfig.ActKeyTap, Keycode = 0x05 };
        m.Steps[3] = new KbConfig.Step { Action = KbConfig.ActRandDelay, DelayMinMs = 300, DelayMaxMs = 60 };
        return cfg;
    }

    /// <summary>把宏 0 的步骤压成可比较的短串</summary>
    private static string Steps(KbConfig c)
    {
        var m = c.Macros[0];
        return string.Join("|", Enumerable.Range(0, m.StepCount).Select(i =>
        {
            var s = m.Steps[i];
            return s.Action switch
            {
                KbConfig.ActKeyTap    => $"TAP{s.Keycode:X2}",
                KbConfig.ActCombo     => $"CMB{s.Keycode:X2}",
                KbConfig.ActKeyDown   => $"DOWN{s.Keycode:X2}",
                KbConfig.ActKeyUp     => $"UP{s.Keycode:X2}",
                KbConfig.ActDelay     => $"DLY{s.DelayMinMs}",
                KbConfig.ActRandDelay => $"RAND{s.DelayMinMs}/{s.DelayMaxMs}",
                _ => $"A{s.Action}",
            };
        }));
    }

    /// <summary>
    /// 找出每一行的 [↑][↓][✕] 三个按钮。
    /// 按控件树的深度优先顺序收集，所以列表下标就是行号。
    /// </summary>
    private static List<(Button up, Button down, Button del)> FindStepOps(Control root)
    {
        var result = new List<(Button, Button, Button)>();

        foreach (var flp in Descendants(root).OfType<FlowLayoutPanel>())
        {
            var btns = flp.Controls.OfType<Button>().ToList();
            if (btns.Count != 3) continue;
            if (!btns.Any(b => b.Text == "✕")) continue;

            result.Add((btns[0], btns[1], btns[2]));
        }
        return result;
    }

    private static Button? FindAddButton(Control root)
        => Descendants(root).OfType<Button>().FirstOrDefault(b => b.Text.Contains("加入步骤"));

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }
}
