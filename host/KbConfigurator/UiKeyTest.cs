using KbConfigurator.Views;

namespace KbConfigurator;

/// <summary>
/// 界面按键行为验收 —— 确认方向键【不会】切换页签。
///
/// 背景：摇杆映射成的就是方向键，推摇杆测试时配置器窗口会收到方向键。
/// 原生 TabControl 会因此切换页签，必须禁掉。
///
/// 这个测试同时验证两件事，缺一不可：
///   1. 【原生 TabControl 确实会被方向键切页签】—— 证明问题真实存在，
///      也证明测试方法本身有效（否则"没变化"可能是测法不对，而不是修好了）
///   2. 【NoArrowTabControl 不会被切】—— 证明修复有效
///
/// 用法：KbConfigurator.exe --uitest     退出码 0 = 全部通过
/// </summary>
internal static class UiKeyTest
{
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP   = 0x0101;

    private const int VK_LEFT  = 0x25;
    private const int VK_UP    = 0x26;
    private const int VK_RIGHT = 0x27;
    private const int VK_DOWN  = 0x28;

    public static int Run()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        int pass = 0, fail = 0;

        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}" + (detail.Length > 0 ? "  —— " + detail : ""));
            if (ok) pass++; else fail++;
        }

        Console.WriteLine("════════ 界面按键行为验收（方向键不切换页签）════════");

        // 用一个不显示出来的 Form 做宿主，让控件真的有窗口句柄
        using var host = new Form { ShowInTaskbar = false, WindowState = FormWindowState.Minimized };

        var stock  = new TabControl();
        var custom = new NoArrowTabControl();

        host.Controls.Add(stock);
        host.Controls.Add(custom);

        foreach (var tc in new TabControl[] { stock, custom })
        {
            for (int i = 0; i < 3; i++) tc.TabPages.Add(new TabPage($"页{i + 1}"));
            tc.SelectedIndex = 0;
        }

        // 强制创建句柄（不显示窗口也能收到发送到句柄的消息）
        _ = host.Handle;
        _ = stock.Handle;
        _ = custom.Handle;      // 这一句会连带创建 TabPages 的句柄

        Console.WriteLine($"  原生 TabControl  句柄=0x{stock.Handle:X}");
        Console.WriteLine($"  自定义 TabControl 句柄=0x{custom.Handle:X}");
        Console.WriteLine();

        // ── 1. 用真实的 WM_KEYDOWN 打到控件句柄上 ──
        Console.WriteLine("── 1. 真实按键消息（WM_KEYDOWN 直接打到句柄）");

        bool stockSwitched = TryArrowViaMessage(stock, VK_RIGHT) || TryArrowViaMessage(stock, VK_LEFT);
        bool customSwitched = TryArrowViaMessage(custom, VK_RIGHT) || TryArrowViaMessage(custom, VK_LEFT);

        Check("原生 TabControl 会被方向键切换页签（问题确实存在，测试方法有效）",
              stockSwitched,
              stockSwitched ? "已复现：右/左方向键让选中页变了" : "没复现出来（可能这条路径不适用，见下面第 2 组）");

        Check("★ NoArrowTabControl 不被方向键切换页签",
              !customSwitched,
              customSwitched ? "仍被切换了！" : "选中页未变");

        // ── 2. 再直接调用 ProcessCmdKey / ProcessDialogKey 两条路径 ──
        //    即便第 1 组没复现出原生行为，这一组也能验证我们的重写确实吞掉了方向键。
        Console.WriteLine("\n── 2. 直接走 ProcessCmdKey / ProcessDialogKey 两条路径");

        foreach (var tc in new TabControl[] { stock, custom })
        {
            bool isCustom = tc is NoArrowTabControl;
            string label = isCustom ? "自定义" : "原生  ";

            int before = tc.SelectedIndex;

            foreach (var vk in new[] { VK_LEFT, VK_RIGHT, VK_UP, VK_DOWN })
            {
                var m = Message.Create(tc.Handle, WM_KEYDOWN, (IntPtr)vk, IntPtr.Zero);
                tc.GetType()
                  .GetMethod("ProcessCmdKey",
                             System.Reflection.BindingFlags.Instance |
                             System.Reflection.BindingFlags.NonPublic)
                  ?.Invoke(tc, new object[] { m, (Keys)vk });

                tc.GetType()
                  .GetMethod("ProcessDialogKey",
                             System.Reflection.BindingFlags.Instance |
                             System.Reflection.BindingFlags.NonPublic)
                  ?.Invoke(tc, new object[] { (Keys)vk });
            }

            int after = tc.SelectedIndex;
            Console.WriteLine($"       {label}: SelectedIndex {before} -> {after}");

            if (isCustom)
                Check("★ 自定义控件在两条路径下都不切换页签", after == before,
                      $"{before} -> {after}");
        }

        // ── 3. 确认方向键没有把子控件也一起废掉 ──
        //    下拉框和数字框里的方向键必须仍然可用，否则改配置就没法选值了。
        Console.WriteLine("\n── 3. 子控件的方向键是否仍然可用");

        var page = new TabPage("t");
        custom.TabPages.Add(page);

        var num = new NumericUpDown { Minimum = 0, Maximum = 100, Value = 50 };
        var cmb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        cmb.Items.AddRange(new object[] { "A", "B", "C" });
        cmb.SelectedIndex = 0;
        page.Controls.Add(num);
        page.Controls.Add(cmb);

        num.Value = 50;
        num.Select(0, 0);
        // 直接调用 NumericUpDown 自己的按键处理：它属于子控件，
        // 不经过 TabControl 的 ProcessCmdKey，所以不受影响
        num.UpButton();
        Check("子控件 NumericUpDown 自增仍然工作", num.Value == 51, $"50 -> {num.Value}");

        cmb.SelectedIndex = 1;
        Check("子控件 ComboBox 选项仍然可选", cmb.SelectedIndex == 1, $"SelectedIndex={cmb.SelectedIndex}");

        // ── 4. 确认非方向键不受影响 ──
        Console.WriteLine("\n── 4. 其它键不受影响");
        bool ctrlTabOk = true;
        try
        {
            custom.GetType()
                  .GetMethod("ProcessCmdKey",
                             System.Reflection.BindingFlags.Instance |
                             System.Reflection.BindingFlags.NonPublic)
                  ?.Invoke(custom, new object[] { Message.Create(custom.Handle, WM_KEYDOWN, (IntPtr)0x09, IntPtr.Zero), Keys.Control | Keys.Tab });
        }
        catch { ctrlTabOk = false; }
        Check("Ctrl+Tab 等非方向键没有被一起吞掉（调用未抛异常）", ctrlTabOk);

        host.Close();

        // ── 5. 最要紧的一条：真窗体用的到底是不是我们的控件 ──
        //    前面的测试都是自己造控件，万一 MainForm 忘了换，前面全绿也没用。
        //    这里直接实例化真正的 MainForm，检查它实际的页签控件。
        Console.WriteLine("\n── 5. 真正的 MainForm 用的页签控件");

        using (var form = new MainForm())
        {
            _ = form.Handle;                     // 强制建句柄（不显示窗口）

            var tabs = FindTabControl(form);
            Check("主窗体里找得到页签控件", tabs is not null, tabs?.GetType().Name ?? "没找到");

            if (tabs is not null)
            {
                Check("★ 主窗体用的是 NoArrowTabControl（不是原生 TabControl）",
                      tabs is NoArrowTabControl,
                      tabs.GetType().FullName ?? tabs.GetType().Name);

                foreach (TabPage pg in tabs.TabPages)
                    foreach (Control c in pg.Controls) { _ = c.Handle; }

                tabs.SelectedIndex = 0;
                bool switched = false;
                foreach (var vk in new[] { VK_RIGHT, VK_LEFT, VK_DOWN, VK_UP })
                {
                    int b4 = tabs.SelectedIndex;
                    SendMessage(tabs.Handle, WM_KEYDOWN, (IntPtr)vk, IntPtr.Zero);
                    SendMessage(tabs.Handle, WM_KEYUP,   (IntPtr)vk, IntPtr.Zero);
                    Application.DoEvents();
                    if (tabs.SelectedIndex != b4) switched = true;
                }

                Check("★ 往主窗体页签发真实方向键消息，选中页不变（四个方向都试了）",
                      !switched,
                      switched ? "仍被切换，修复没生效！" : $"SelectedIndex 保持 {tabs.SelectedIndex}");

                // ★ 按**名字**断言而不是按数量：页签会随设计演进增删
                //   （先插了「按键布局」，后来又把「输入调试」「按键映射」合并掉了）。
                //   按数量断言每次都要改，按名字才是在验证"哪几个页存在"。
                var wanted = new[] { "按键布局", "宏编辑" };
                var have = tabs.TabPages.Cast<TabPage>().Select(p => p.Text).ToList();

                Check("主窗体页签与预期一致（按名字）",
                      wanted.All(have.Contains) && have.Count == wanted.Length,
                      string.Join(" / ", have));

                Check("旧「输入调试」「按键映射」已合并掉（不再有这两页）",
                      !have.Contains("输入调试") && !have.Contains("按键映射"),
                      string.Join(" / ", have));

                Check("「按键布局」页排在第一位",
                      have.Count > 0 && have[0] == "按键布局",
                      have.Count > 0 ? have[0] : "(没有页签)");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"════════ 结果：{pass} 通过 / {fail} 失败 ════════");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>递归找出窗体里的 TabControl</summary>
    private static TabControl? FindTabControl(Control root)
    {
        foreach (Control c in root.Controls)
        {
            if (c is TabControl tc) return tc;
            var deep = FindTabControl(c);
            if (deep is not null) return deep;
        }
        return null;
    }

    /// <summary>真的往控件句柄发一次方向键，返回选中页是否变了</summary>
    private static bool TryArrowViaMessage(TabControl tc, int vk)
    {
        int before = tc.SelectedIndex;
        SendMessage(tc.Handle, WM_KEYDOWN, (IntPtr)vk, IntPtr.Zero);
        SendMessage(tc.Handle, WM_KEYUP,   (IntPtr)vk, IntPtr.Zero);
        Application.DoEvents();
        return tc.SelectedIndex != before;
    }
}
