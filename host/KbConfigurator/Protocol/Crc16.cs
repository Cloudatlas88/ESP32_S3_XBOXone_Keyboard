namespace KbConfigurator.Protocol;

/// <summary>
/// CRC-16/CCITT-FALSE —— 必须与固件 crc16.c 完全一致
///
/// 多项式 0x1021，初值 0xFFFF，不反转，不异或输出。
/// 测试向量：crc16("123456789") == 0x29B1
/// </summary>
public static class Crc16
{
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in data)
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 0x8000) != 0
                    ? (ushort)((crc << 1) ^ 0x1021)
                    : (ushort)(crc << 1);
            }
        }
        return crc;
    }

    /// <summary>自检：失败说明实现与固件不一致，所有配置读写都会 CRC 失败</summary>
    public static bool SelfTest()
        => Compute("123456789"u8) == 0x29B1 && Compute(ReadOnlySpan<byte>.Empty) == 0xFFFF;
}
