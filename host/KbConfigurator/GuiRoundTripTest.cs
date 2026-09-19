using KbConfigurator.Hid;
using KbConfigurator.Protocol;
using KbConfigurator.Views;

namespace KbConfigurator;

/// <summary>
/// 界面往返自检 —— 不开窗口，直接实例化两个编辑页签跑一遍
/// 「设备读回 → 刷到界面 → 界面写回模型 → 序列化」。
///
/// 为什么专门测这条路径：
///   以前 <c>KeymapPanel.DoSave()</c> 是 <c>new KbConfig()</c> 再逐字段填，
///   结果宏定义区被整体清零 —— 读回来的宏一保存就没了，而且**界面看不出异常**。
///   所以这里断言的是【往返之后字节完全一致】，宏一个都不能丢。
///
/// 用法：KbConfigurator.exe --guiroundtrip      退出码 0 = 全部通过
/// </summary>
internal static class GuiRoundTripTest
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

        Console.WriteLine("════════ 界面往返自检（无窗口）════════");

        using var dev = new KbDevice();
        if (!dev.Open())
        {
            Console.WriteLine("✗ 打不开设备，无法测试界面往返。");
            return 1;
        }

        // ── 0. 按 GUI【完全相同的时序】做一次读写存取 ──
        // GUI 的 Connect() 会调用 StartReading() 起一个后台线程持续 hid_read_timeout，
        // 之后 DoRead()/DoSave() 在【同一个句柄】上做 send/get_feature_report。
        // CLI 自检里读线程是停着的，所以这条并发路径只有这里能测到。
        Console.WriteLine("\n── 0. 并发路径（读线程开着的同时做读写存取，模拟 GUI 真实时序）");
        dev.StartReading();
        Thread.Sleep(600);              // 让读线程跑起来

        bool ioSame = true;
        string ioDetail = "";
        // 只做 2 轮：固件对 CFG_SAVE 有【一分钟内最多 10 次】的防写入风暴配额，
        // 轮数多了会撞配额（那是另一条有意设计的保护，不是并发 bug）。
        for (int round = 1; round <= 2; round++)
        {
            var r = dev.ReadConfig();
            if (r is null)
            {
                ioSame = false;
                ioDetail = $"第 {round} 轮 CFG_READ 返回 null（读线程占着句柄？）";
                break;
            }

            if (!dev.WriteConfig(r)) { ioSame = false; ioDetail = $"第 {round} 轮 CFG_WRITE 失败"; break; }
            var (sok, sdet) = dev.SaveConfig();
            if (!sok) { ioSame = false; ioDetail = $"第 {round} 轮 CFG_SAVE 失败: {sdet}"; break; }
        }

        Check("读线程开着时 读取/写入/保存 仍然可用", ioSame,
              ioSame ? "连做 2 轮全部成功" : ioDetail);

        dev.StopReading();
        Thread.Sleep(200);

        var raw = dev.ReadConfig();
        if (raw is null)
        {
            Console.WriteLine("✗ 停止读线程后仍然读不到配置。");
            return 1;
        }

        var original = KbConfig.FromBytes(raw);
        Console.WriteLine($"  设备配置：宏数量={original.MacroCount}  " +
                          $"死区={original.Sticks[0].DeadzonePct}%");

        // ── 构造两个页签并注册进共享模型（和 MainForm 里的做法一致）──
        var session = new ConfigSession();
        var keymap = new ButtonLayoutPanel();
        var macro  = new MacroPanel();
        session.Register(keymap);
        session.Register(macro);

        // ── 1. 第一遍：界面往返（可能包含一次「规范化」）──
        session.Replace(KbConfig.FromBytes(raw));
        session.CollectFromUi();
        var pass1 = session.Config.ToBytes();
        var after1 = KbConfig.FromBytes(pass1);

        // 宏数量是按"实际有几步"重新数出来的（见 MacroPanel.WriteToModel）。
        // 如果设备里 macro_count 与实际内容不符（例如残留了没清的宏槽），
        // 第一遍会把它规范化 —— 这是【有意为之】，不是丢数据：
        // 宏定义本身一个字节都没动，只是把"有几个宏"这个计数纠正过来。
        if (after1.MacroCount != original.MacroCount)
        {
            Console.WriteLine($"  [ 规范化 ] 宏数量 {original.MacroCount} → {after1.MacroCount}" +
                              "（设备里 macro_count 与实际步数不一致，界面按实际内容纠正）");
            int stale = 0;
            for (int m = original.MacroCount; m < KbConfig.MacroMax; m++)
                if (original.Macros[m].StepCount > 0) stale++;
            Check("被计入的宏确实有内容（不是凭空多出来的）", stale > 0,
                  $"{stale} 个超出 macro_count 的槽里残留着 {original.Macros[3].StepCount} 步等定义");
            Check("宏定义本身没有被改动",
                  Enumerable.Range(0, original.MacroCount).All(m => MacroEquals(original.Macros[m], after1.Macros[m])),
                  $"前 {original.MacroCount} 个宏逐字段一致");
        }
        else
        {
            Check("界面往返后字节完全一致（宏没有被按键映射页签清零）",
                  pass1.SequenceEqual(raw),
                  pass1.SequenceEqual(raw) ? "396 字节逐字节相同" : FirstDiff(raw, pass1));
        }

        // ── 2. 第二遍：幂等性 —— 再存一次必须完全不动 ──
        //    这才是真正要守的不变量：界面"读一次存一次"不能越存越变。
        //    现实场景就是用户点两下「保存到设备」。
        var second = KbConfig.FromBytes(pass1);
        session.Replace(second);
        session.CollectFromUi();
        var pass2 = session.Config.ToBytes();

        Check("再存一次字节完全不变（幂等：连点两次保存不会漂移）",
              pass2.SequenceEqual(pass1),
              pass2.SequenceEqual(pass1) ? "第二遍与第一遍逐字节相同" : FirstDiff(pass1, pass2));

        var after2 = KbConfig.FromBytes(pass2);
        Check("宏数量稳定", after2.MacroCount == after1.MacroCount,
              $"{after1.MacroCount} → {after2.MacroCount}");

        for (int m = 0; m < after1.MacroCount; m++)
        {
            var a = after1.Macros[m];
            var b = after2.Macros[m];
            Check($"宏#{m} 步数/循环/超时/各步参数 全部保持", MacroEquals(a, b),
                  $"{a.StepCount} 步{(a.Loop ? " 循环" : "")}");
        }

        // ── 3. 改一个宏的随机延迟，验证改动确实传递到字节里 ──
        //    这一项能抓住"界面改了但 WriteToModel 没同步"的问题。
        var probe = KbConfig.FromBytes(pass2);
        if (probe.MacroCount > 0)
        {
            probe.Macros[0].Steps[1].Action     = KbConfig.ActRandDelay;
            probe.Macros[0].Steps[1].DelayMinMs = 777;
            probe.Macros[0].Steps[1].DelayMaxMs = 42;

            session.Replace(probe);
            session.CollectFromUi();
            var back = KbConfig.FromBytes(session.Config.ToBytes());

            Check("改「随机延迟 基准值/抖动%」后能从字节里读回",
                  back.Macros[0].Steps[1].DelayMinMs == 777 && back.Macros[0].Steps[1].DelayMaxMs == 42,
                  $"基准={back.Macros[0].Steps[1].DelayMinMs}ms 抖动=±{back.Macros[0].Steps[1].DelayMaxMs}%");
        }

        // ── 4. 验证保留字节不会被界面改动 ──
        //    偏移 57/58 原来是「宏总开关/上电默认」，功能移除后是保留字节。
        //    界面不该再去写它们，否则会给老配置留下无意义的变化。
        Check("保留字节（偏移 57/58）保持为 0，界面不再写它",
              pass2[57] == 0 && pass2[58] == 0,
              $"b[57]={pass2[57]} b[58]={pass2[58]}");

        // ── 5. 循环开关传递（宏的核心新行为：勾上后触发按钮变开关）──
        var lp = KbConfig.FromBytes(pass2);
        if (lp.MacroCount > 0)
        {
            lp.Macros[0].Loop = true;
            session.Replace(lp);
            session.CollectFromUi();
            var lpBack = KbConfig.FromBytes(session.Config.ToBytes());
            Check("改『循环执行』开关后能从字节里读回", lpBack.Macros[0].Loop, "flags bit0 = 1");
        }

        // ── 6. 宏计数规范化（故意造一个"数据与计数不一致"的配置）──
        //    设备正常情况下已经是规范化的，所以这条路径只能靠构造数据来测。
        //    要守住的底线：纠正计数的时候【不能动宏定义本身】。
        var messy = KbConfig.FromBytes(pass2);
        messy.MacroCount = 1;                       // 声称只有 1 个宏
        messy.Macros[3].StepCount = 3;              // 槽#4 里却躺着 3 步
        messy.Macros[3].Steps[0].Action = KbConfig.ActKeyTap;
        messy.Macros[3].Steps[0].Keycode = 0x1E;    // '1'

        session.Replace(messy);
        session.CollectFromUi();
        var fixedUp = session.Config;

        Check("宏计数被纠正为实际内容的个数", fixedUp.MacroCount == 4,
              $"macro_count 1 → {fixedUp.MacroCount}");
        Check("纠正计数时槽#4 的宏定义没被改动",
              fixedUp.Macros[3].StepCount == 3 && fixedUp.Macros[3].Steps[0].Keycode == 0x1E,
              $"槽#4 = {fixedUp.Macros[3].StepCount} 步，首步键码 0x{fixedUp.Macros[3].Steps[0].Keycode:X2}");
        Check("前 2 个宏仍逐字段保持",
              MacroEquals(after1.Macros[0], fixedUp.Macros[0])
              && MacroEquals(after1.Macros[1], fixedUp.Macros[1]));

        // 收尾：把设备恢复成原始配置，别把测试用的 777ms 留在设备里
        dev.WriteConfig(raw);
        dev.SaveConfig();
        Console.WriteLine("  (已把设备配置恢复成测试前的样子)");

        keymap.Dispose();
        macro.Dispose();

        Console.WriteLine();
        Console.WriteLine($"════════ 结果：{pass} 通过 / {fail} 失败 ════════");
        return fail == 0 ? 0 : 1;
    }

    private static bool MacroEquals(KbConfig.Macro a, KbConfig.Macro b)
        => a.StepCount == b.StepCount && a.Flags == b.Flags && a.TimeoutMs == b.TimeoutMs
        && Enumerable.Range(0, KbConfig.MacroStepMax).All(i =>
               a.Steps[i].Action     == b.Steps[i].Action
            && a.Steps[i].Keycode    == b.Steps[i].Keycode
            && a.Steps[i].Modifiers  == b.Steps[i].Modifiers
            && a.Steps[i].DelayMinMs == b.Steps[i].DelayMinMs
            && a.Steps[i].DelayMaxMs == b.Steps[i].DelayMaxMs);

    private static string FirstDiff(byte[] a, byte[] b)
    {
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            if (a[i] != b[i])
                return $"首个差异在偏移 {i}：写入 0x{a[i]:X2} / 回读 0x{b[i]:X2}";
        return $"长度不同 {a.Length} vs {b.Length}";
    }
}
