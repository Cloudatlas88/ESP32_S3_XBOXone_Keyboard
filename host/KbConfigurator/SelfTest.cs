using KbConfigurator.Hid;
using KbConfigurator.Protocol;

namespace KbConfigurator;

/// <summary>
/// 无界面自检 —— 不开窗口直接验证 HID 通信链路。
///
/// 用法：KbConfigurator.exe --selftest [秒数]
/// 退出码 0 表示全部通过。
/// </summary>
internal static class SelfTest
{
    /// <summary>
    /// reserved2 保留区的起始偏移。
    /// = 头 8 + stick[2]×8 + buttons[24]×8 + macro_count 1 = **217**（保留 3 字节在 217..219）
    /// ★ 用公式算而不是写死数字：0x0003 时是 57，0x0004 起是 217 ——
    ///   写死的话每次升配置版本都要回来找一遍（这次就是因为写死 57 而报了一条假失败）。
    /// </summary>
    private static int ReservedOff => 8 + KbConfig.StickCount * 8 + KbConfig.SlotCount * 8 + 1;

    /// <summary>把配置里 reserved2 保留区改成旧版的值，并重算 CRC —— 用来验证兼容性</summary>
    private static byte[] MutateReserved(byte[] cfg, byte swBtn, byte defaultOn)
    {
        var b = (byte[])cfg.Clone();
        b[ReservedOff] = swBtn;
        b[ReservedOff + 1] = defaultOn;
        var crc = Crc16.Compute(b.AsSpan(KbConfig.CrcOffset));
        b[6] = (byte)(crc & 0xFF);
        b[7] = (byte)(crc >> 8);
        return b;
    }

    public static int Run(int seconds = 3)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        int pass = 0, fail = 0;

        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  —— " + detail : "")}");
            if (ok) pass++; else fail++;
        }

        Console.WriteLine("════════ ESP32-S3 键盘配置器 自检 ════════");
        Console.WriteLine($"hidapi: {System.Runtime.InteropServices.Marshal.PtrToStringAnsi(HidNative.hid_version_str())}");
        Console.WriteLine();

        // ── 0. 本地算法自检 ──
        Console.WriteLine("── 0. 本地算法自检");
        Check("CRC16/CCITT-FALSE 测试向量", Crc16.SelfTest(), "crc16(\"123456789\") == 0x29B1");
        {
            var def = KbConfig.CreateDefault();
            var (cfgOk, cfgReason) = KbConfig.Validate(def.ToBytes());
            Check("配置结构体自检 (magic/version/size/CRC)", cfgOk, cfgReason);

            // 保留字节（原宏总开关/上电默认，功能已移除）必须写 0，
            // 且不再参与校验 —— 老配置里存着旧值也要能正常加载
            var probe = KbConfig.CreateDefault();
            var pb = probe.ToBytes();
            // ★ 读配置的包预算必须覆盖整个配置。
            //   这条是给"写死 16 包"那个坑加的：配置涨到 956 字节后
            //   需要 18 包，写死 16 会让 ReadConfig 静默返回 null，
            //   而现象（"CFG_READ 无响应"）指向的是设备侧，极难联想到这里。
            {
                int need = (KbConfig.Size + KbDevice.PktDataMax - 1) / KbDevice.PktDataMax;
                Check($"读配置的包预算够用（预算 {KbDevice.ReadPacketBudget} 包 ≥ 需要 {need} 包）",
                      KbDevice.ReadPacketBudget >= need,
                      $"{KbConfig.Size} 字节 / 每包 {KbDevice.PktDataMax} 字节");
            }

            Check($"偏移 {ReservedOff}..{ReservedOff + 2} 是保留字节（写 0）",
                  pb[ReservedOff] == 0 && pb[ReservedOff + 1] == 0 && pb[ReservedOff + 2] == 0,
                  $"b[{ReservedOff}..{ReservedOff + 2}] = {pb[ReservedOff]},{pb[ReservedOff + 1]},{pb[ReservedOff + 2]}");
            Check("保留字节填任何值都能通过校验（兼容老配置）",
                  KbConfig.Validate(MutateReserved(pb, 3, 1)).Ok,
                  "老配置里 macro_switch_btn=3/默认=1 仍可加载");

            // 随机延迟：基准值 + 抖动%
            var st = new KbConfig.Step { Action = KbConfig.ActRandDelay, DelayMinMs = 200, DelayMaxMs = 30 };
            Check("随机延迟描述（基准 + 抖动%）", st.ToString().Contains("200ms") && st.ToString().Contains("30%"),
                  st.ToString());
        }

        // ── 1. 枚举 ──
        Console.WriteLine("── 1. HID 接口枚举");
        var all = KbDevice.Enumerate();
        Check("枚举到设备", all.Count > 0, $"{all.Count} 个顶层集合");
        foreach (var i in all)
            Console.WriteLine($"       if={i.InterfaceNumber,-3} {i.UsageText}   {i.Manufacturer} / {i.Product}   " +
                              $"SN:{(string.IsNullOrEmpty(i.Serial) ? "（无）" : i.Serial)}");

        var vendor = KbDevice.FindVendorCollection(all);
        Check("找到厂商配置集合 (UP:0xFF00)", vendor is not null,
              vendor is null ? "" : $"{vendor.UsageText}  if={vendor.InterfaceNumber}");

        if (vendor is null)
        {
            Console.WriteLine("\n✗ 找不到厂商配置集合，后续检查跳过。");
            return 1;
        }

        // ── 2. 打开 ──
        Console.WriteLine("\n── 2. 打开设备（必须用 open_path）");
        using var dev = new KbDevice();
        Check("hid_open_path 成功", dev.Open(),
              $"{dev.ManufacturerString} / {dev.ProductString}  SN:{(string.IsNullOrEmpty(dev.SerialString) ? "（无）" : dev.SerialString)}");

        if (!dev.IsOpen) return 1;

        // ── 3. Feature Report 握手 ──
        Console.WriteLine("\n── 3. Feature Report 握手 (RID 0x03)");
        var raw = dev.GetFeatureReport(KbDevice.RidConfig);
        if (raw is null)
        {
            Check("读取 Feature Report", false, "返回 null");
        }
        else
        {
            Check("读取 Feature Report", raw.Length == KbDevice.ConfigReportSize,
                  $"{raw.Length} 字节：{Convert.ToHexString(raw.AsSpan(0, Math.Min(8, raw.Length)))} …");
        }
        var (ok, detail) = dev.Probe();
        Check("握手内容校验", ok, detail);

        // ── 4. 输入上报 ──
        Console.WriteLine($"\n── 4. 持续读取 Input Report {seconds} 秒 (RID 0x04)");
        int frames = 0;
        InputState? last = null;
        var lockObj = new object();
        var done = new ManualResetEventSlim(false);

        dev.InputReceived += st => { lock (lockObj) { frames++; last = st; } };
        dev.StartReading();

        var t0 = DateTime.Now;
        while ((DateTime.Now - t0).TotalSeconds < seconds)
        {
            Thread.Sleep(200);
            if (last is not null)
            {
                var s = last;
                Console.Write($"\r  左 X={s.Raw[0],4}({s.Pct[0],+4}%) Y={s.Raw[1],4}({s.Pct[1],+4}%)  " +
                              $"右 X={s.Raw[2],4}({s.Pct[2],+4}%) Y={s.Raw[3],4}({s.Pct[3],+4}%)  " +
                              $"槽位={s.Slots & 0xFFFFFF:X6} 帧={frames,4}   ");
            }
        }
        Console.WriteLine();
        dev.StopReading();

        Check("收到 Input Report", frames > 0, $"{frames} 帧 / {seconds} 秒（期望约 {seconds * 20} 帧）");
        if (last is not null)
        {
            var s = last;
            Check("USB 已挂载标志", s.UsbMounted);
            // ★ 右摇杆没接线时会被固件判为故障并禁用（这是正确行为），
            //   所以只把"左摇杆两轴健康"作为硬判据。
            Check("左摇杆两轴均健康", !s.FaultOf(0) && !s.FaultOf(1),
                  s.FaultOf(0) || s.FaultOf(1) ? s.FaultText() : $"中心=({s.Center[0]},{s.Center[1]})");
            for (int a = 0; a < InputState.AxisCount; a++)
            {
                string[] nm = { "左X", "左Y", "右X", "右Y" };
                Console.WriteLine($"       {nm[a]}: 行程[{s.TMin[a]},{s.TMax[a]}] 跨度{s.SpanOf(a)}  "
                                + (s.FaultOf(a) ? "【已禁用】" : s.TravelLearnedOf(a) ? "(已收敛)" : "(仍为满量程)"));
            }
            Console.WriteLine($"       宏状态: {(s.MacroBusy ? "有宏正在执行" : "空闲")}");
        }

        // ── 5. 配置协议 ──
        Console.WriteLine("\n── 5. 配置协议 (Feature Report)");
        var (iok, info, idetail) = dev.GetInfo();
        Check("GET_INFO", iok, idetail);

        if (info is { StructMatches: true })
        {
            var cfgRaw = dev.ReadConfig();
            Check("CFG_READ 读回完整配置", cfgRaw is { Length: KbConfig.Size },
                  cfgRaw is null ? "无响应" : $"{cfgRaw.Length} 字节");

            if (cfgRaw is not null)
            {
                var (vok, vreason) = KbConfig.Validate(cfgRaw);
                Check("配置结构校验 (magic/version/size/CRC)", vok, vreason);

                if (vok)
                {
                    var cfg = KbConfig.FromBytes(cfgRaw);
                    Console.WriteLine($"       死区 左{cfg.Sticks[0].DeadzonePct}%/右{cfg.Sticks[1].DeadzonePct}%  "
                                    + $"摇杆参数 左反向={cfg.Sticks[0].InvertX}/{cfg.Sticks[0].InvertY} "
                                    + $"右反向={cfg.Sticks[1].InvertX}/{cfg.Sticks[1].InvertY}");
                    Console.WriteLine($"       {KbConfig.SlotCount} 槽位=" + string.Join(' ',
                                      cfg.Buttons.Select((b, i) => $"{SlotMap.NameOf(i)}:{b.Keycode:X2}")));

                    // 原样写回（不改值，避免打乱用户配置），只为打通写入路径
                    Check("CFG_WRITE 分片写入", dev.WriteConfig(cfgRaw),
                          $"{cfgRaw.Length} 字节 / " +
                          $"{(cfgRaw.Length + KbDevice.PktDataMax - 1) / KbDevice.PktDataMax} 包");
                    var (sok, sdetail) = dev.SaveConfig();
                    Check("CFG_SAVE 提交并落盘", sok, sdetail);

                    // 只读状态查询：必须不改变设备状态，否则不能用于轮询
                    var s1 = dev.QueryStatus();
                    Check("只读状态查询 STATUS (0x09)",
                          s1 is not null,
                          s1 is null ? "无应答"
                                     : $"执行中={(s1.Busy ? "是" : "否")} " +
                                       $"累计触发={s1.RunCount} 中止={s1.AbortCount} 步数={s1.StepCount}");

                    var s2 = dev.QueryStatus();
                    Check("STATUS 查询自身不改变状态",
                          s1 is not null && s2 is not null && s1.Busy == s2.Busy,
                          $"两次读到 执行中={(s2?.Busy == true ? "是" : "否")}");

                    // 废弃的 0x07（宏总开关）应当被识别为未知命令
                    var dead = dev.Transact(0x07, data: new byte[] { 0xFF });
                    Check("废弃的 0x07 宏总开关命令已被拒绝（未知命令）",
                          dead is null || dead.IsNack,
                          dead is null ? "无应答" : $"NACK err=0x{dead.ErrCode:X2}");
                }
            }
        }
        else
        {
            Console.WriteLine("       (跳过：设备未实现配置协议或结构体不匹配)");
        }

        dev.Close();

        Console.WriteLine();
        Console.WriteLine($"════════ 结果：{pass} 通过 / {fail} 失败 ════════");
        return fail == 0 ? 0 : 1;
    }
}
