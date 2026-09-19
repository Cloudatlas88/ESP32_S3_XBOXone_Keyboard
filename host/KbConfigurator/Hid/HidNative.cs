using System.Runtime.InteropServices;

namespace KbConfigurator.Hid;

/// <summary>
/// hidapi 原生库 P/Invoke 声明
///
/// 对应官方 libusb/hidapi 0.15.0 Windows x64 构建（host/native/hidapi-x64.dll）。
/// 原生文件名固定为 hidapi.dll（由 csproj 的 &lt;Link&gt; 重命名后拷到输出目录）。
/// </summary>
internal static class HidNative
{
    private const string Dll = "hidapi.dll";

    // ── 生命周期 ──
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int hid_init();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int hid_exit();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr hid_version_str();

    // ── 枚举 ──
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr hid_enumerate(ushort vendorId, ushort productId);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void hid_free_enumeration(IntPtr devs);

    // ── 打开 / 关闭 ──
    // ★ 必须用 open_path：设备有两个 HID 接口，hid_open(vid,pid,...) 只会打开第一个，
    //   而那是被 Windows kbdhid 独占的键盘接口，必然失败。
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr hid_open_path([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void hid_close(IntPtr dev);

    // ── 读 ──
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int hid_read_timeout(IntPtr dev, byte[] data, nuint length, int milliseconds);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int hid_set_nonblocking(IntPtr dev, int nonblock);

    // ── Feature Report ──
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int hid_get_feature_report(IntPtr dev, byte[] data, nuint length);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int hid_send_feature_report(IntPtr dev, byte[] data, nuint length);

    // ── 字符串描述符 ──
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int hid_get_manufacturer_string(IntPtr dev, [Out] char[] s, nuint maxlen);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int hid_get_product_string(IntPtr dev, [Out] char[] s, nuint maxlen);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int hid_get_serial_number_string(IntPtr dev, [Out] char[] s, nuint maxlen);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern IntPtr hid_error(IntPtr dev);

    /// <summary>
    /// hid_device_info 结构体（hidapi 0.15.0，x64）
    ///
    /// ⚠️ 字段顺序必须与 hidapi.h 完全一致。0.15.0 里 <c>next</c> 在 <c>bus_type</c> 之前，
    ///    顺序写错不会报错，只会读到垃圾数据 —— 这是 P/Invoke 最典型的坑。
    ///
    /// C 侧布局（x64）：
    ///   0  char*               path
    ///   8  unsigned short      vendor_id
    ///  10  unsigned short      product_id
    ///  16  wchar_t*            serial_number        (对齐到 8)
    ///  24  unsigned short      release_number
    ///  32  wchar_t*            manufacturer_string  (对齐到 8)
    ///  40  wchar_t*            product_string
    ///  48  unsigned short      usage_page
    ///  50  unsigned short      usage
    ///  52  int                 interface_number
    ///  56  hid_device_info*    next                 (对齐到 8)
    ///  64  hid_bus_type        bus_type
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct HidDeviceInfo
    {
        internal IntPtr Path;
        internal ushort VendorId;
        internal ushort ProductId;
        internal IntPtr SerialNumber;
        internal ushort ReleaseNumber;
        internal IntPtr ManufacturerString;
        internal IntPtr ProductString;
        internal ushort UsagePage;
        internal ushort Usage;
        internal int InterfaceNumber;
        internal IntPtr Next;
        internal int BusType;
    }

    /// <summary>把一个 UTF-16 指针解成 C# 字符串（NULL 返回 null）</summary>
    internal static string? PtrToString(IntPtr p)
        => p == IntPtr.Zero ? null : Marshal.PtrToStringUni(p);
}
