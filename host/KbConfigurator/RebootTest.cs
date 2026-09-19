using KbConfigurator.Hid;
using KbConfigurator.Protocol;

namespace KbConfigurator;

/// <summary>
/// 「保存 → 软重启 → 自动重连 → 回读比对」端到端验收。
///
/// 验的是用户真正关心的事：**保存之后软重启一次，配置还在不在。**
/// 这条链路把三件事串起来了：
///   1. 固件 0x0A 软重启命令（不必拔插 USB）
///   2. 重启前强制落盘（否则刚点的「保存」会丢）
///   3. 句柄失效检测 + 自动重连（设备重启后重新枚举）
///
/// 用法：KbConfigurator.exe --reboot     退出码 0 = 全部通过
/// </summary>
internal static class RebootTest
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

        Console.WriteLine("════════ 软重启 + NVS 持久化 验收 ════════");

        using var dev = new KbDevice();
        if (!dev.Open())
        {
            Console.WriteLine("✗ 打不开设备。");
            return 1;
        }
        dev.StartReading();
        Thread.Sleep(800);

        var raw0 = dev.ReadConfig();
        if (raw0 is null) { Console.WriteLine("✗ 读不到配置。"); return 1; }

        var cfg0 = KbConfig.FromBytes(raw0);
        Console.WriteLine($"  重启前：死区={cfg0.Sticks[0].DeadzonePct}%  宏数量={cfg0.MacroCount}");

        // ── 1. 改一个能被观察到的值并保存 ──
        //    死区在 10~15 之间来回切，改完一定要能读回同一个值。
        byte probeDz = (byte)(cfg0.Sticks[0].DeadzonePct == 10 ? 15 : 10);

        var cfg1 = KbConfig.FromBytes(raw0);
        cfg1.Sticks[0].DeadzonePct = probeDz;
        var raw1 = cfg1.ToBytes();

        Check("写入待验证的配置（死区改为 " + probeDz + "%）", dev.WriteConfig(raw1));
        var (sok, sdet) = dev.SaveConfig();
        Check("CFG_SAVE 提交", sok, sdet);

        Thread.Sleep(400);
        var back1 = dev.ReadConfig();
        var cfgBack1 = back1 is null ? null : KbConfig.FromBytes(back1);
        Check("重启前回读确认改动已生效", cfgBack1?.Sticks[0].DeadzonePct == probeDz,
              $"死区={cfgBack1?.Sticks[0].DeadzonePct}%");

        // ── 2. 发软重启命令 ──
        var res = dev.RebootDevice();
        Check("软重启命令被接受", res.Ok, res.Detail);
        Check("固件确认重启前已落盘", res.Flushed, res.Flushed ? "已落盘" : "落盘失败（改动可能丢）");

        if (!res.Ok) { dev.Close(); Console.WriteLine($"\n════════ 结果：{pass} 通过 / {fail} 失败 ════════"); return 1; }

        // 关键：命令返回时设备【还没】重启，ACK 能正常收到。
        // 如果固件在这里直接 esp_restart()，上面那条就永远拿不到应答。

        // ── 3a. 先等设备【真的重启】（句柄失效）──
        //     ★ 这一步不能省：固件是延迟 800ms 才重启的，
        //       如果一上来就忙着 Reopen，会在设备还没重启时重连到【同一个旧实例】，
        //       然后它 800ms 后重启，刚拿到的句柄又失效了 —— 看起来像"重连成功但用不了"。
        //       实测踩过：不等待时 Reopen 在 615ms 就"成功"，紧接着读配置就失败。
        Console.WriteLine($"\n  先等设备真正重启（固件延迟 {res.DelayMs}ms）…");

        bool died = false;
        var tDead = DateTime.Now;
        while ((DateTime.Now - tDead).TotalSeconds < 20)
        {
            if (!dev.Ping()) { died = true; break; }
            Thread.Sleep(200);
        }

        Check("设备确实重启了（原句柄已失效）", died,
              died ? $"{(DateTime.Now - tDead).TotalSeconds:F2}s 后检测到"
                   : "20s 内句柄一直有效，重启命令可能没生效");

        if (!died) { dev.Close(); Console.WriteLine($"\n════════ 结果：{pass} 通过 / {fail} 失败 ════════"); return 1; }

        // ── 3b. 再等它重新枚举回来 ──
        Console.WriteLine("  等待设备重新枚举回来…");

        bool reopened = false;
        var t0 = DateTime.Now;
        for (int attempt = 1; attempt <= 30 && !reopened; attempt++)
        {
            Thread.Sleep(500);
            reopened = dev.Reopen();
        }
        var backMs = (DateTime.Now - t0).TotalMilliseconds;

        Check("重启后自动重连成功", reopened, $"{backMs:F0}ms（等待并重开）");

        if (!reopened) { dev.Close(); Console.WriteLine($"\n════════ 结果：{pass} 通过 / {fail} 失败 ════════"); return 1; }

        dev.StartReading();
        Thread.Sleep(1000);

        Check("重连后能收到 Input Report（设备确实起来了）",
              (DateTime.Now - dev.LastFrameAt).TotalSeconds < 1.5,
              $"最近一帧 {(DateTime.Now - dev.LastFrameAt).TotalSeconds:F2}s 前");

        var (pok, pdet) = dev.Probe();
        Check("重连后握手通过", pok, pdet);

        // ── 4. 核心：重启后配置还在不在 ──
        var raw2 = dev.ReadConfig();
        Check("重启后能读回配置", raw2 is not null, raw2 is null ? "读不到" : $"{raw2.Length} 字节");

        if (raw2 is not null)
        {
            var cfg2 = KbConfig.FromBytes(raw2);

            Check("★ 重启后配置与重启前【逐字节一致】（NVS 持久化成功）",
                  raw2.SequenceEqual(raw1),
                  raw2.SequenceEqual(raw1) ? "396 字节完全相同" : FirstDiff(raw1, raw2));

            Check($"★ 改动的死区 {probeDz}% 在重启后依然保持",
                  cfg2.Sticks[0].DeadzonePct == probeDz,
                  $"重启后死区={cfg2.Sticks[0].DeadzonePct}%");
        }

        // ── 5. 收尾：恢复原来的死区 ──
        var restore = KbConfig.FromBytes(raw0);
        dev.WriteConfig(restore.ToBytes());
        var (rok, rdet) = dev.SaveConfig();
        Check("已把死区改回原值 " + cfg0.Sticks[0].DeadzonePct + "%", rok, rdet);

        dev.Close();

        Console.WriteLine();
        Console.WriteLine($"════════ 结果：{pass} 通过 / {fail} 失败 ════════");
        return fail == 0 ? 0 : 1;
    }

    private static string FirstDiff(byte[] a, byte[] b)
    {
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            if (a[i] != b[i])
                return $"首个差异在偏移 {i}：重启前 0x{a[i]:X2} / 重启后 0x{b[i]:X2}";
        return $"长度不同 {a.Length} vs {b.Length}";
    }
}
