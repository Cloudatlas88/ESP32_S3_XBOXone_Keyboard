"""从 COM3（UART，CH343）抓完整启动日志。

为什么不用 COM4：那里是原生 USB 口，复位时会重新枚举，已经打开的句柄会失效、
抓不到开头几行 —— 而恰恰是开头几行才会说清 TinyUSB 为什么没起来。

★ 打开 COM3 后必须立刻把 DTR/RTS 拉到无效：
  CH343 上这两个信号接着 ESP32 的 EN / GPIO0，拉错就把芯片按在复位或下载模式里，
  表现是"串口完全没输出"（前面就这么白忙了一轮）。
"""
import sys, time, threading

import serial

# ★ 端口从命令行取，别写死。
#   Windows 会在设备复位后重新分配 COM 号 —— 实测 CH343 从 COM3 变成过 COM5，
#   写死的话下次就是"串口打不开"，还得先去怀疑是不是板子坏了。
#   用法：python tools/bootlog.py COM5
PORT = sys.argv[1] if len(sys.argv) > 1 else 'COM3'
SECONDS = 12

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

p = serial.Serial()
p.port = PORT
p.baudrate = 115200
p.timeout = 0.3
p.dtr = False          # 关键：别碰 EN / GPIO0
p.rts = False
p.open()
time.sleep(0.2)
p.reset_input_buffer()

buf = []


def reader():
    t0 = time.time()
    while time.time() - t0 < SECONDS:
        try:
            d = p.read(8192)
            if d:
                buf.append(d.decode('utf-8', 'replace'))
        except Exception:
            pass


th = threading.Thread(target=reader)
th.start()
time.sleep(0.8)

# 用 RTS 硬复位（RTS 接 EN）：拉低 -> 释放
p.rts = True
time.sleep(0.15)
p.rts = False
print(f'（已通过 {PORT} 的 RTS 复位芯片）')

th.join()
p.close()

text = ''.join(buf)
if not text.strip():
    print('!! 一个字节都没收到 —— 检查 TX 是否接上，或芯片是否被按在复位里')
for line in text.splitlines():
    if line.strip():
        print(line)
