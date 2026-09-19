using KbConfigurator.Hid;

namespace KbConfigurator;

/// <summary>
/// 句柄失效检测 + 自动重连的验收测试。
///
/// 复现的场景就是用户实际遇到的那个：
///   GUI 先启动并连上设备，之后设备【重新枚举】（拔插 USB 线，或固件复位导致
///   重新枚举）—— 此时 Windows 会把原来的句柄作废，但 IsOpen 仍是 true，
///   界面照样显示"已连接"，而每一次读取/保存都静默失败。
///
/// 测试步骤：
///   1. 打开设备并开始接收 Input Report，确认健康
///   2. 等待外部把设备关掉再启用（相当于重插），观察是否被检测为失效
///   3. 执行与界面相同的恢复动作（Reopen + StartReading + Probe）
///   4. 确认恢复后 读取/写入/保存 都恢复正常
///
/// 用法：KbConfigurator.exe --reconnect [等待秒数]
/// 退出码 0 = 全部通过
/// </summary>
internal static class ReconnectTest
{
    public static int Run(int timeoutSec = 60)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        int pass = 0, fail = 0;

        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}" + (detail.Length > 0 ? "  —— " + detail : ""));
            if (ok) pass++; else fail++;
        }

        Console.WriteLine("════════ 句柄失效检测 + 自动重连 验收 ════════");

        using var dev = new KbDevice();
        if (!dev.Open())
        {
            Console.WriteLine("✗ 打不开设备。");
            return 1;
        }
        dev.StartReading();
        Console.WriteLine($"  已打开：{dev.ProductString}  路径={dev.OpenedPath}");

        // ── 1. 健康状态：不应被判为失效 ──
        Thread.Sleep(1500);
        var before = dev.LastFrameAt;
        Check("健康时能收到 Input Report（20Hz）",
              (DateTime.Now - before).TotalSeconds < 1.0,
              $"最近一帧 {(DateTime.Now - before).TotalSeconds:F2}s 前");
        Check("健康时 Ping 通过", dev.Ping());
        Check("健康时【不会】被误判为失效", !dev.LooksDead(out var healthyWhy), healthyWhy);

        // 顺带确认健康时读写存取正常
        var ok0 = dev.ReadConfig() is not null;
        Check("健康时 CFG_READ 正常", ok0);

        // ── 2. 等待外部让设备重新枚举 ──
        Console.WriteLine($"\n  等待设备重新枚举（最多 {timeoutSec} 秒）…");
        Console.WriteLine("  （由外部脚本 Disable/Enable 该设备来模拟拔插）");

        var t0 = DateTime.Now;
        bool detected = false;
        string why = "";
        while ((DateTime.Now - t0).TotalSeconds < timeoutSec)
        {
            if (dev.LooksDead(out why)) { detected = true; break; }
            Thread.Sleep(250);
        }

        var waited = (DateTime.Now - t0).TotalSeconds;
        Console.Write($"\r  检测耗时 {waited:F1}s                                        \n");

        Check("设备重新枚举后被检测出句柄失效", detected, detected ? why : $"等待 {timeoutSec}s 仍未检测到");
        if (!detected) { dev.Close(); Console.WriteLine($"\n════════ 结果：{pass} 通过 / {fail} 失败 ════════"); return 1; }

        // ── 3. 执行与界面相同的恢复动作 ──
        //    真实场景里用户拔掉再插上，设备会离开几秒 —— 所以给足耐心。
        //    （界面里 PollDevice 每 1.5s 还会再试，比这里更宽松。）
        int reopenBefore = dev.ReopenCount;
        var tRec = DateTime.Now;

        bool reopened = false;
        for (int attempt = 1; attempt <= 20 && !reopened; attempt++)
        {
            reopened = dev.Reopen();
            if (!reopened) Thread.Sleep(500);      // 设备可能还没枚举完，等一会儿再试
        }
        var recMs = (DateTime.Now - tRec).TotalMilliseconds;

        Check("Reopen 成功（自动重连）", reopened,
              reopened ? $"{recMs:F0}ms，第 {dev.ReopenCount - reopenBefore} 次尝试" : "重试 20 次仍失败");

        if (!reopened) { dev.Close(); Console.WriteLine($"\n════════ 结果：{pass} 通过 / {fail} 失败 ════════"); return 1; }

        dev.StartReading();
        Thread.Sleep(1200);

        var (probeOk, probeDetail) = dev.Probe();
        Check("重连后握手通过", probeOk, probeDetail);

        Check("重连后又能收到 Input Report",
              (DateTime.Now - dev.LastFrameAt).TotalSeconds < 1.0,
              $"最近一帧 {(DateTime.Now - dev.LastFrameAt).TotalSeconds:F2}s 前");

        Check("重连后不再被判为失效", !dev.LooksDead(out var afterWhy), afterWhy);

        // ── 4. 重连后读写存取必须恢复 ──
        var raw = dev.ReadConfig();
        Check("重连后 CFG_READ 恢复", raw is not null, raw is null ? "读不到" : $"{raw.Length} 字节");

        if (raw is not null)
        {
            var written = dev.WriteConfig(raw);
            Check("重连后 CFG_WRITE 恢复", written);

            var (sok, sdetail) = dev.SaveConfig();
            Check("重连后 CFG_SAVE 恢复", sok, sdetail);
        }

        var st = dev.QueryStatus();
        Check("重连后 STATUS 只读查询恢复", st is not null,
              st is null ? "无应答" : $"执行中={(st.Busy ? "是" : "否")}");

        dev.Close();

        Console.WriteLine();
        Console.WriteLine($"════════ 结果：{pass} 通过 / {fail} 失败 ════════");
        return fail == 0 ? 0 : 1;
    }
}
