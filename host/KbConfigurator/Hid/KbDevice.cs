using System.Runtime.InteropServices;
using KbConfigurator.Protocol;

namespace KbConfigurator.Hid;

/// <summary>一个已枚举到的 HID 顶层集合</summary>
public sealed record KbHidInterface(
    string  Path,
    int     InterfaceNumber,
    ushort  UsagePage,
    ushort  Usage,
    string? Manufacturer,
    string? Product,
    string? Serial)
{
    public string UsageText => $"UP=0x{UsagePage:X4} U=0x{Usage:X4}";
}

/// <summary>
/// ESP32 键盘设备的通信封装。
///
/// 关键点：设备是「复合设备 + 2 个 HID 接口」，
///   MI_00 = 键盘（被 Windows kbdhid 独占，打不开）
///   MI_01 = 厂商自定义集合 UP:0xFF00（本类要连的就是它）
///
/// 所以必须 hid_enumerate → 按 UsagePage==0xFF00 挑出来 → hid_open_path。
/// 用 hid_open(vid,pid,...) 会打开第一个接口（键盘），必然失败。
/// </summary>
public sealed class KbDevice : IDisposable
{
    public const ushort Vid = 0x3554;
    public const ushort Pid = 0xFA09;

    /// <summary>厂商自定义集合的 Usage Page（我们自己定义的）</summary>
    public const ushort VendorUsagePage = 0xFF00;

    public const byte RidConfig = 0x03;   // Feature Report
    public const byte RidInput  = 0x04;   // Input Report
    public const int  ConfigReportSize = 64;
    public const int  ReadBufferSize   = 64;

    /// <summary>配置通道协议版本（与固件 KB_PROTO_VERSION 对应）</summary>
    public const byte ProtoVersion = 0x01;

    private IntPtr _dev = IntPtr.Zero;
    private Thread? _readThread;
    private volatile bool _running;

    public bool IsOpen => _dev != IntPtr.Zero;

    /// <summary>产品字符串（来自设备描述符）</summary>
    public string? ProductString { get; private set; }
    public string? ManufacturerString { get; private set; }
    public string? SerialString { get; private set; }

    /// <summary>当前打开的 hidapi 设备路径（用来判断是不是换了一个设备实例）</summary>
    public string? OpenedPath { get; private set; }

    /// <summary>打开至今经历过几次重连（诊断用）</summary>
    public int ReopenCount { get; private set; }

    /// <summary>最近一次收到 Input Report 的时刻（存活检测用）</summary>
    public DateTime LastFrameAt { get; private set; } = DateTime.MinValue;

    /// <summary>每收到一帧 Input Report 触发一次（在后台线程上）</summary>
    public event Action<InputState>? InputReceived;

    /// <summary>收到非输入帧（例如意外 Report ID）时触发</summary>
    public event Action<string>? Message;

    // ══════════════════════════════════════════════════════════════
    //  枚举
    // ══════════════════════════════════════════════════════════════

    /// <summary>枚举指定 VID/PID 下全部 HID 顶层集合（含真实接收器，便于对比）</summary>
    public static List<KbHidInterface> Enumerate(ushort vid = Vid, ushort pid = Pid)
    {
        var list = new List<KbHidInterface>();
        IntPtr head = HidNative.hid_enumerate(vid, pid);
        if (head == IntPtr.Zero) return list;

        try
        {
            IntPtr cur = head;
            while (cur != IntPtr.Zero)
            {
                var info = Marshal.PtrToStructure<HidNative.HidDeviceInfo>(cur);

                list.Add(new KbHidInterface(
                    Path:            Marshal.PtrToStringUTF8(info.Path) ?? "",
                    InterfaceNumber: info.InterfaceNumber,
                    UsagePage:       info.UsagePage,
                    Usage:           info.Usage,
                    Manufacturer:    HidNative.PtrToString(info.ManufacturerString),
                    Product:         HidNative.PtrToString(info.ProductString),
                    Serial:          HidNative.PtrToString(info.SerialNumber)));

                cur = info.Next;
            }
        }
        finally
        {
            HidNative.hid_free_enumeration(head);
        }
        return list;
    }

    /// <summary>从枚举结果里挑出我们的厂商配置集合（必须是 UP:0xFF00）</summary>
    public static KbHidInterface? FindVendorCollection(List<KbHidInterface> all)
        => all.FirstOrDefault(i => i.UsagePage == VendorUsagePage);

    // ══════════════════════════════════════════════════════════════
    //  打开 / 关闭
    // ══════════════════════════════════════════════════════════════

    public bool Open()
    {
        Close();

        var all = Enumerate();
        var vendor = FindVendorCollection(all);
        if (vendor is null) return false;

        _dev = HidNative.hid_open_path(vendor.Path);
        if (_dev == IntPtr.Zero) return false;

        OpenedPath         = vendor.Path;
        SerialString       = vendor.Serial;
        ProductString      = vendor.Product;
        ManufacturerString = vendor.Manufacturer;
        LastFrameAt        = DateTime.Now;

        HidNative.hid_set_nonblocking(_dev, 0);   // 阻塞读（配 read_timeout 用）
        return true;
    }

    /// <summary>
    /// 重新枚举并重新打开设备。
    ///
    /// ★ 为什么必须有这个：USB 设备一旦被拔掉再插上（或者固件复位导致重新枚举），
    ///   Windows 会把原来的文件句柄作废，但 <see cref="IsOpen"/> 仍然返回 true
    ///   （IntPtr 非零）—— 于是界面照样显示"已连接"，而之后每一次
    ///   Feature Report 收发都失败，用户看到的就是"读取和保存都无效"。
    ///
    ///   注意：hidapi 的设备路径里那段实例 ID 是跟【物理端口】绑定的，
    ///   同一个口重插后路径字符串往往一模一样，所以【不能只比对路径】，
    ///   必须真的做一次收发来确认句柄还活着（见 <see cref="Ping"/>）。
    /// </summary>
    public bool Reopen()
    {
        ReopenCount++;
        Close();
        return Open();
    }

    /// <summary>
    /// 句柄存活探测：做一次 Feature Report 往返。
    /// 句柄失效时 hidapi 会返回 -1，这里就返回 false。
    /// </summary>
    public bool Ping()
    {
        if (!IsOpen) return false;
        try
        {
            var r = GetFeatureReport(RidConfig);
            return r is not null && r.Length >= ConfigReportSize;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>默认的"输入上报断流多久算可疑"阈值</summary>
    public static readonly TimeSpan DefaultSilenceLimit = TimeSpan.FromSeconds(2.5);

    /// <summary>
    /// 判断当前句柄是不是已经失效了。
    ///
    /// 两步判据，缺一不可：
    ///   1. 【输入上报断流】—— 设备活着时每秒约 20 帧，句柄失效后一帧都收不到。
    ///      这条零成本，不用额外发请求。
    ///   2. 【Feature Report 收发失败】—— 光凭断流会误判（比如主机偶尔卡顿），
    ///      所以再做一次真实往返确认。
    ///
    /// ★ 为什么不比对设备路径：hidapi 路径里那段实例 ID 是跟【物理端口】绑定的，
    ///   同一个口重插后路径字符串往往一模一样 —— 只比路径会漏判。
    /// </summary>
    public bool LooksDead(out string why) => LooksDead(DefaultSilenceLimit, out why);

    public bool LooksDead(TimeSpan silenceLimit, out string why)
    {
        why = "";

        if (!IsOpen) { why = "尚未打开设备"; return true; }

        var silence = DateTime.Now - LastFrameAt;
        if (silence < silenceLimit)
        {
            why = $"输入上报正常（{silence.TotalSeconds:F1}s 前还有帧）";
            return false;
        }

        if (Ping())
        {
            why = $"输入上报断流 {silence.TotalSeconds:F1}s，但 Feature Report 仍通（判定为未失效）";
            return false;
        }

        why = $"输入上报断流 {silence.TotalSeconds:F1}s，且 Feature Report 收发失败";
        return true;
    }

    /// <summary>读 Feature Report（Report ID 由 data[0] 指定）</summary>
    public byte[]? GetFeatureReport(byte reportId, int length = ConfigReportSize)
    {
        if (!IsOpen) return null;

        var buf = new byte[length];
        buf[0] = reportId;

        int n = HidNative.hid_get_feature_report(_dev, buf, (nuint)length);
        if (n <= 0) return null;

        return buf.Take(n).ToArray();
    }

    /// <summary>写 Feature Report（data[0] 必须是 Report ID）</summary>
    public bool SendFeatureReport(byte[] data)
    {
        if (!IsOpen) return false;
        return HidNative.hid_send_feature_report(_dev, data, (nuint)data.Length) >= 0;
    }

    // ══════════════════════════════════════════════════════════════
    //  配置通道协议
    //
    //  帧（负载 63 字节）：
    //    0     CMD     1
    //    1     SEQ     1
    //    2     FLAGS   1   bit0 = LAST
    //    3-4   OFF     2 LE
    //    5-6   TOTAL   2 LE
    //    7-8   CRC16   2 LE  仅最后一包有效：整个配置的 CRC16
    //    9-62  DATA    54
    //
    //  交互模型「先 SET 再 GET」：SET 下达命令，GET 取回应答。
    // ══════════════════════════════════════════════════════════════

    public const byte CmdGetInfo    = 0x01;
    public const byte CmdCfgRead    = 0x02;
    public const byte CmdCfgWrite   = 0x03;
    public const byte CmdCfgSave    = 0x04;
    public const byte CmdCfgReset   = 0x05;
    public const byte CmdCalibReset = 0x06;
    // 0x07 原来是「切换宏总开关」。总开关功能已移除，命令码废弃。
    public const byte CmdMacroRun   = 0x08;   // 触发/开关宏：DATA[0] = 宏索引；0xFF = 中止
    public const byte CmdStatus     = 0x09;   // 只读查询运行状态
    public const byte CmdReboot     = 0x0A;   // 软重启设备：DATA[0..1] = 延迟毫秒
    public const byte CmdAck        = 0x80;
    public const byte CmdNack       = 0x81;

    /// <summary>触发宏时 DATA[0] 的特殊值：中止当前宏</summary>
    public const byte MacroRunAbort = 0xFF;

    public const int PktDataMax = 54;
    private const int PktData = 9;

    public static readonly Dictionary<byte, string> ErrText = new()
    {
        [0x00] = "OK",            [0x01] = "未知命令",   [0x02] = "参数错误",
        [0x03] = "CRC 校验失败",   [0x04] = "长度超限",   [0x05] = "忙",
        [0x0A] = "写入过于频繁",   [0x0B] = "NVS 写入失败", [0x0C] = "版本不兼容",
    };

    public sealed record DeviceInfo(
        byte Proto, ushort StructSize, byte MaxPayload, ushort CfgVersion,
        int FwMajor, int FwMinor, int FwPatch, bool Dirty, ushort Epoch)
    {
        public bool StructMatches => StructSize == KbConfig.Size;
    }

    public sealed record Response(byte Cmd, byte Seq, byte Flags, ushort Off,
                                  ushort Total, ushort Crc, byte[] Data)
    {
        public bool IsAck  => Cmd == CmdAck;
        public bool IsNack => Cmd == CmdNack;

        public byte ErrCode => (IsNack && Data.Length > 1) ? Data[1] : (byte)0;
        public string ErrTextOf() => ErrCode == 0 ? "OK"
            : KbDevice.ErrText.TryGetValue(ErrCode, out var s) ? s : $"0x{ErrCode:X2}";
    }

    /// <summary>发一条 SET 命令并取回 GET 应答</summary>
    public Response? Transact(byte cmd, byte seq = 0, ushort off = 0, ushort total = 0,
                              byte flags = 0, ushort crc = 0, ReadOnlySpan<byte> data = default)
    {
        if (!IsOpen) return null;

        var payload = new byte[KB_CFG_PAYLOAD];
        payload[0] = cmd;
        payload[1] = seq;
        payload[2] = flags;
        BitConverter.TryWriteBytes(payload.AsSpan(3), off);
        BitConverter.TryWriteBytes(payload.AsSpan(5), total);
        BitConverter.TryWriteBytes(payload.AsSpan(7), crc);
        data.CopyTo(payload.AsSpan(PktData));

        var frame = new byte[ConfigReportSize];
        frame[0] = RidConfig;
        payload.CopyTo(frame.AsSpan(1));

        if (HidNative.hid_send_feature_report(_dev, frame, (nuint)frame.Length) < 0)
            return null;

        var r = GetFeatureReport(RidConfig);
        if (r is null || r.Length < ConfigReportSize) return null;

        return new Response(
            r[1], r[2], r[3],
            (ushort)(r[4] | (r[5] << 8)),
            (ushort)(r[6] | (r[7] << 8)),
            (ushort)(r[8] | (r[9] << 8)),
            r.Skip(PktData + 1).ToArray());     // +1 跳过 Report ID
    }

    private const int KB_CFG_PAYLOAD = 63;

    /// <summary>握手 + 读取设备能力（GET_INFO）。设备默认应答就是它，直接 GET 即可。</summary>
    public (bool Ok, DeviceInfo? Info, string Detail) GetInfo()
    {
        if (!IsOpen) return (false, null, "设备未打开");

        var r = GetFeatureReport(RidConfig);
        if (r is null || r.Length < ConfigReportSize)
            return (false, null, "读取 Feature Report 失败");

        if (r[1] != CmdGetInfo)
            return (false, null, $"应答命令异常 0x{r[1]:X2}（期望 GET_INFO）");

        var d = r.AsSpan(PktData + 1);          // +1 跳过 Report ID
        if (d.Length < 12) return (false, null, "GET_INFO 数据太短");

        var info = new DeviceInfo(
            Proto:       d[0],
            StructSize:  (ushort)(d[1] | (d[2] << 8)),
            MaxPayload:  d[3],
            CfgVersion:  (ushort)(d[4] | (d[5] << 8)),
            FwMajor:     d[6], FwMinor: d[7], FwPatch: d[8],
            Dirty:       (d[9] & 1) != 0,
            Epoch:       (ushort)(d[10] | (d[11] << 8)));

        var ok = info.Proto == ProtoVersion && info.StructMatches;
        var detail = ok
            ? $"协议 0x{info.Proto:X2}  结构 {info.StructSize} 字节  固件 Ver{info.FwMajor * 100 + info.FwMinor * 10 + info.FwPatch:D3}"
            : $"协议或结构体不匹配（协议 0x{info.Proto:X2}，结构 {info.StructSize}，本地 {KbConfig.Size}）";

        return (ok, info, detail);
    }

    /// <summary>兼容旧调用的握手（内部走 GetInfo）</summary>
    public (bool Ok, string Detail) Probe()
    {
        var (ok, _, detail) = GetInfo();
        return (ok, detail);
    }

    /// <summary>分片读回完整配置（164 字节）</summary>
    /// <summary>
    /// 读一次完整配置最多要发几包。
    ///
    /// ★ 必须**由配置长度算出来**，不能写死。
    ///   原实现写死 `guard < 16`；配置从 572 涨到 956 字节后需要
    ///   ceil(956/54) = 18 包，循环在第 16 包就退出，outBuf 只有 864 字节，
    ///   末尾的 `Count == Size` 判定不成立 → 直接返回 null，
    ///   表现为"CFG_READ 无响应" —— 而设备其实完全正常，
    ///   很容易被误判成固件或接线坏了。（+2 是给分片/重传留的余量。）
    /// </summary>
    internal static int ReadPacketBudget => (KbConfig.Size + PktDataMax - 1) / PktDataMax + 2;

    public byte[]? ReadConfig()
    {
        var outBuf = new List<byte>(KbConfig.Size);
        ushort off = 0;

        for (int guard = 0; guard < ReadPacketBudget; guard++)
        {
            var r = Transact(CmdCfgRead, off: off);
            if (r is null) return null;
            if (r.IsNack) { Message?.Invoke($"CFG_READ 被拒: {r.ErrTextOf()}"); return null; }

            int n = Math.Min(PktDataMax, r.Total - off);
            if (n <= 0) break;
            outBuf.AddRange(r.Data.Take(n));
            off += (ushort)n;

            if ((r.Flags & 0x01) != 0 || off >= r.Total) break;
        }

        return outBuf.Count == KbConfig.Size ? outBuf.ToArray() : null;
    }

    /// <summary>分片写入配置（只进设备暂存区，不落 NVS）</summary>
    public bool WriteConfig(byte[] cfg)
    {
        ushort off = 0;
        byte seq = 0;

        while (off < cfg.Length)
        {
            int n = Math.Min(PktDataMax, cfg.Length - off);
            bool last = off + n >= cfg.Length;
            ushort crc = last ? Crc16.Compute(cfg) : (ushort)0;

            var r = Transact(CmdCfgWrite, seq: seq, off: off, total: (ushort)cfg.Length,
                             flags: (byte)(last ? 1 : 0), crc: crc,
                             data: cfg.AsSpan(off, n));
            if (r is null) return false;
            if (r.IsNack) { Message?.Invoke($"CFG_WRITE seq={seq} 被拒: {r.ErrTextOf()}"); return false; }

            off += (ushort)n;
            seq++;
        }
        return true;
    }

    /// <summary>提交并落盘（固件约 100ms 内写入 NVS）</summary>
    public (bool Ok, string Detail) SaveConfig()
    {
        var r = Transact(CmdCfgSave);
        if (r is null) return (false, "无应答");
        if (r.IsNack) return (false, $"被拒: {r.ErrTextOf()}");

        int pending = r.Data.Length >= 4 ? (r.Data[2] | (r.Data[3] << 8)) : 0;
        return (true, pending > 0 ? $"已生效，约 {pending}ms 内落盘" : "已生效并落盘");
    }

    public (bool Ok, string Detail) FactoryReset()
    {
        var r = Transact(CmdCfgReset);
        if (r is null) return (false, "无应答");
        return r.IsNack ? (false, $"被拒: {r.ErrTextOf()}") : (true, "已恢复出厂默认");
    }

    /// <summary>重置摇杆校准（调用时务必让摇杆静止）</summary>
    public (bool Ok, string Detail) ResetCalibration()
    {
        var r = Transact(CmdCalibReset);
        if (r is null) return (false, "无应答");
        return r.IsNack ? (false, $"被拒: {r.ErrTextOf()}")
                        : (true, "已重置：行程恢复满量程并重新自学习");
    }

    /// <summary>
    /// 触发 / 开关一个宏。
    ///
    /// 走的是固件的 <c>macro_toggle_or_trigger()</c>，和实体按键**完全同一条路**：
    ///   · 勾了「循环执行」的宏 → 开关式：没在跑就开、正在跑就停
    ///   · 非循环宏           → 每次从头跑
    /// 所以界面上「测试触发」测出来的行为，就是按键触发的行为。
    /// </summary>
    public (bool Ok, string Detail) RunMacro(byte macroId)
    {
        Span<byte> d = stackalloc byte[1];
        d[0] = macroId;

        var r = Transact(CmdMacroRun, data: d);
        if (r is null) return (false, "无应答");
        if (r.IsNack)  return (false, $"被拒: {r.ErrTextOf()}");

        // ★ DATA[2] 是【本次动作意图】：1 = 已启动/触发，0 = 已停止。
        //   不能拿"这一刻是否在执行"来判断 —— 宏是先入队、稍后才真正启动的，
        //   刚触发那一刻读到的是 false，界面就会把"刚启动"显示成"已停止"。
        bool started = r.Data.Length > 2 && r.Data[2] != 0;

        return (true, macroId == MacroRunAbort
            ? "已请求中止当前宏"
            : $"已{(started ? "启动" : "停止")}宏 {macroId + 1}");
    }

    /// <summary>中止当前正在执行的宏（会释放所有由宏按下的键）</summary>
    public (bool Ok, string Detail) AbortMacro() => RunMacro(MacroRunAbort);

    /// <summary>设备运行状态（只读查询，<see cref="CmdStatus"/>）</summary>
    public sealed record RunStatus(bool Busy, byte CurrentMacro,
                                   uint RunCount, uint AbortCount, uint StepCount,
                                   byte Modifiers, byte PressedCount);

    /// <summary>软重启的结果</summary>
    public sealed record RebootResult(bool Ok, ushort DelayMs, bool Flushed, string Detail);

    /// <summary>
    /// 软重启设备 —— 不需要拔插 USB。
    ///
    /// 用途：验证配置是否真的写进了 NVS（掉电不丢），以及把设备恢复成刚上电的状态。
    ///
    /// ⚠️ 设备不会立刻重启：固件会先把这个 ACK 发出来，等
    ///    <paramref name="delayMs"/> 毫秒（0 = 用固件默认 800ms）后才
    ///    <c>esp_restart()</c>。因为本协议是「先 SET 后 GET」，
    ///    立刻重启的话上位机根本取不到应答，会把成功的重启误报成失败。
    ///
    /// 重启后设备会重新枚举，调用方的自动重连逻辑会把它接回来
    /// （见 <see cref="Reopen"/> 与 <c>MainForm.PollDevice</c>）。
    /// </summary>
    public RebootResult RebootDevice(ushort delayMs = 0)
    {
        Span<byte> d = stackalloc byte[2];
        d[0] = (byte)(delayMs & 0xFF);
        d[1] = (byte)(delayMs >> 8);

        var r = Transact(CmdReboot, data: d);
        if (r is null) return new RebootResult(false, 0, false, "无应答（设备可能已经开始重启）");
        if (r.IsNack)  return new RebootResult(false, 0, false, $"被拒: {r.ErrTextOf()}");

        ushort dly   = r.Data.Length >= 3 ? (ushort)(r.Data[1] | (r.Data[2] << 8)) : (ushort)0;
        bool flushed = r.Data.Length >= 4 && r.Data[3] != 0;
        return new RebootResult(true, dly, flushed, $"设备将在 {dly}ms 后重启");
    }

    /// <summary>
    /// 只读查询运行状态。不会改变设备任何状态 ——
    /// 所以可以放心用于轮询/计时。
    /// </summary>
    public RunStatus? QueryStatus()
    {
        var r = Transact(CmdStatus);
        if (r is null || r.IsNack || r.Data.Length < 16) return null;

        return new RunStatus(
            // DATA[1] 原来是宏总开关状态，总开关移除后改为保留字节，不再读取
            Busy:         r.Data[2] != 0,
            CurrentMacro: r.Data[3],
            RunCount:     BitConverter.ToUInt32(r.Data, 4),
            AbortCount:   BitConverter.ToUInt32(r.Data, 8),
            StepCount:    BitConverter.ToUInt32(r.Data, 12),
            // ★ 键盘报告的实际状态 —— 用它能验证"松开之后修饰键有没有真的清掉"
            Modifiers:    r.Data[16],
            PressedCount: r.Data[17]);
    }

    // ══════════════════════════════════════════════════════════════
    //  输入上报读取线程
    // ══════════════════════════════════════════════════════════════

    public void StartReading()
    {
        if (!IsOpen || _running) return;
        _running = true;

        _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "hid-read" };
        _readThread.Start();
    }

    public void StopReading()
    {
        _running = false;
        _readThread?.Join(500);
        _readThread = null;
    }

    private void ReadLoop()
    {
        var buf = new byte[ReadBufferSize];

        while (_running && IsOpen)
        {
            int n;
            try { n = HidNative.hid_read_timeout(_dev, buf, (nuint)buf.Length, 200); }
            catch { break; }

            if (n <= 0) continue;                    // 超时或出错，继续等

            LastFrameAt = DateTime.Now;

            if (buf[0] != RidInput)
            {
                Message?.Invoke($"收到未知 Report ID 0x{buf[0]:X2}（长度 {n}）");
                continue;
            }

            try
            {
                var st = InputState.Parse(buf.AsSpan(1, Math.Min(n - 1, InputState.PayloadSize)));
                InputReceived?.Invoke(st);
            }
            catch (Exception ex)
            {
                Message?.Invoke($"解析输入帧失败：{ex.Message}");
            }
        }
    }

    public void Close()
    {
        StopReading();
        if (_dev != IntPtr.Zero)
        {
            HidNative.hid_close(_dev);
            _dev = IntPtr.Zero;
        }
        OpenedPath = null;
    }

    public void Dispose() => Close();
}
