#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
配置通道（Feature Report）协议工具 —— 上位机协议的命令行参照实现

同时用于验证固件：读配置、改配置、保存、恢复出厂。

用法:
    python tools/cfg_proto.py info                  # GET_INFO
    python tools/cfg_proto.py read                  # 分片读回并解析配置
    python tools/cfg_proto.py set --deadzone 10     # 改一项
    python tools/cfg_proto.py set --btn1 0x04 --up 0x52
    python tools/cfg_proto.py save                  # 提交 + 落盘
    python tools/cfg_proto.py reset                 # 恢复出厂
    python tools/cfg_proto.py roundtrip             # 端到端自测

帧格式（Report ID + 63 字节负载）:
    0     CMD     1
    1     SEQ     1
    2     FLAGS   1   bit0 = LAST
    3-4   OFF     2   LE
    5-6   TOTAL   2   LE
    7-8   CRC16   2   LE  仅最后一包有效：整个配置的 CRC16
    9-62  DATA    54
"""
import argparse
import struct
import sys
import time

import hid

VID, PID = 0x3554, 0xFA09
USAGE_PAGE = 0xFF00

RID_CFG = 0x03
RID_INPUT = 0x04
FRAME = 64
PKT_DATA_MAX = 54

CMD_GET_INFO = 0x01
CMD_CFG_READ = 0x02
CMD_CFG_WRITE = 0x03
CMD_CFG_SAVE = 0x04
CMD_CFG_RESET = 0x05
CMD_CALIB_RESET = 0x06
# 0x07 原来是「切换宏总开关」。总开关功能已移除，命令码废弃不用
# —— 故意不复用，免得新旧固件/上位机混用时触发到不相关的动作。
CMD_MACRO_RUN = 0x08
CMD_STATUS = 0x09
CMD_REBOOT = 0x0A
CMD_ACK = 0x80
CMD_NACK = 0x81

ERR = {
    0x00: "OK", 0x01: "未知命令", 0x02: "参数错误", 0x03: "CRC 错误",
    0x04: "长度超限", 0x05: "忙", 0x0A: "写入过于频繁", 0x0B: "NVS 写入失败",
    0x0C: "版本不兼容",
}

CFG_MAGIC = 0x4B42
CFG_VERSION = 0x0006          # ★ 宏步数 8 → 20
CFG_SIZE = 956
CFG_CRC_OFFSET = 8            # struct CRC 覆盖 crc16 字段之后的字节
BTN_COUNT = 26          # ★ 槽位数（0x0005 起 = 26）
MACRO_MAX = 4
MACRO_STEP_MAX = 20

MACRO_SW_ABORT = 0xFF         # macro_run 的 DATA[0] 取这个值 = 中止当前宏
MAX_JITTER_PCT = 90           # 随机延迟抖动上限（%）

# 宏 flags
MACRO_FLAG_LOOP = 0x01        # 循环执行 —— 触发按钮变成「开 / 停」开关

# 按钮 flags
BTNFLAG_ENABLED = 0x01
BTNFLAG_HAS_MACRO = 0x02

# 宏动作
ACT_NAMES = {0: "单键敲击", 1: "组合键", 2: "按住", 3: "释放", 4: "固定延迟", 5: "随机延迟"}
ACT_KEY_TAP, ACT_COMBO, ACT_KEY_DOWN, ACT_KEY_UP, ACT_DELAY, ACT_RAND_DELAY = range(6)


# ══════════════════════════ CRC16/CCITT-FALSE ══════════════════════════

def crc16(data: bytes) -> int:
    crc = 0xFFFF
    for b in data:
        crc ^= b << 8
        for _ in range(8):
            crc = ((crc << 1) ^ 0x1021) & 0xFFFF if (crc & 0x8000) else (crc << 1) & 0xFFFF
    return crc


def selfcheck_crc():
    assert crc16(b"123456789") == 0x29B1, hex(crc16(b"123456789"))
    assert crc16(b"") == 0xFFFF


# ══════════════════════════ 配置结构 ══════════════════════════

DIRS = ("up", "down", "left", "right")
STICK_COUNT = 2

# 26 个槽位的名字（与上位机 SlotMap.SlotNames、固件 slot_idx_t 一致）
SLOT_NAMES = [
    "A 键", "B 键", "X 键", "Y 键",
    "LB 肩键", "RB 肩键",
    "View 键", "Menu 键", "Xbox 键", "Share 键",
    "L3", "R3",
    "十字键 ↑", "十字键 ↓", "十字键 ←", "十字键 →",
    "左摇杆 ↑", "左摇杆 ↓", "左摇杆 ←", "左摇杆 →",
    "右摇杆 ↑", "右摇杆 ↓", "右摇杆 ←", "右摇杆 →",
    "LT 扳机", "RT 扳机",
]


def unpack_cfg(b: bytes) -> dict:
    if len(b) != CFG_SIZE:
        raise ValueError(f"配置长度 {len(b)} != {CFG_SIZE}")

    magic, ver, size, crc = struct.unpack_from("<HHHH", b, 0)
    dz, up, dn, lf, rt, ix, iy, _rsv = struct.unpack_from("<8B", b, 8)

    # ★ 0x0004：stick[2] × 8 字节（只放轴向行为：死区/反向/保留）
    sticks = []
    for s in range(STICK_COUNT):
        so = 8 + s * 8
        dz, ix, iy = struct.unpack_from("<3B", b, so)
        sticks.append(dict(deadzone=dz, invert_x=ix, invert_y=iy))

    # ★ buttons[24] × 8 字节（下标即槽位号；摇杆的 8 个方向槽位也在这里）
    btn_base = 8 + STICK_COUNT * 8    # = 24
    btns = []
    for i in range(BTN_COUNT):
        kc, mod, trig, flags, mid, _r0, _r1, _r2 = struct.unpack_from("<8B", b, btn_base + i * 8)
        btns.append(dict(keycode=kc, modifiers=mod, trigger=trig,
                         flags=flags, macro_id=mid))

    off = btn_base + BTN_COUNT * 8    # = 216
    macro_count = b[off]
    # b[off+1] / b[off+2] 原来是「宏总开关按键 / 上电默认启用」，
    # 功能已移除，现在是保留字节，不再解析。
    off += 4

    macros = []
    for _ in range(MACRO_MAX):
        step_count, mflags, timeout = struct.unpack_from("<BBH", b, off)
        off += 4
        steps = []
        for _ in range(MACRO_STEP_MAX):
            act, kc, mod, _r, dmin, dmax = struct.unpack_from("<4BHH", b, off)
            off += 8
            steps.append(dict(action=act, keycode=kc, modifiers=mod,
                              dmin=dmin, dmax=dmax))
        macros.append(dict(step_count=step_count, flags=mflags,
                           timeout=timeout, steps=steps))

    return dict(magic=magic, version=ver, size=size, crc16=crc,
                sticks=sticks,
                invert_x=ix, invert_y=iy, buttons=btns,
                macro_count=macro_count,
                macros=macros)


def pack_cfg(c: dict) -> bytes:
    b = bytearray(CFG_SIZE)
    struct.pack_into("<HHHH", b, 0, CFG_MAGIC, CFG_VERSION, CFG_SIZE, 0)

    # ★ 0x0004：stick[2] × 8 字节，只放轴向行为（死区/反向）；其余保留
    for s in range(STICK_COUNT):
        st = c["sticks"][s]
        struct.pack_into("<3B", b, 8 + s * 8, st["deadzone"], st["invert_x"], st["invert_y"])

    # ★ buttons[24] × 8 字节，下标即槽位号
    btn_base = 8 + STICK_COUNT * 8          # = 24
    for i, bt in enumerate(c["buttons"]):
        struct.pack_into("<8B", b, btn_base + i * 8, bt["keycode"], bt["modifiers"],
                         bt["trigger"], bt["flags"], bt.get("macro_id", 0xFF), 0, 0, 0)

    off = btn_base + BTN_COUNT * 8          # = 216
    b[off] = c.get("macro_count", 0)
    # off+1 / off+2 是保留字节（原宏总开关/上电默认，功能已移除），留 0
    off += 4

    # ★ 每个宏槽固定占 (4 + 8×8) = 68 字节，不管里面实际写了几步。
    #   原实现是 `off += 8` 只在有 step 时才走，于是"步数没填满 8 步"的宏
    #   会让后面的宏写到错误偏移上，把结构写歪。
    #   之所以一直没暴露：调用方恰好都把 steps 补齐到 8 个了 —— 属于运气，不是正确。
    for m in c.get("macros", [])[:MACRO_MAX]:
        struct.pack_into("<BBH", b, off, m["step_count"], m.get("flags", 0),
                         m.get("timeout", 0))
        off += 4
        for si in range(MACRO_STEP_MAX):
            steps = m.get("steps", [])
            s = steps[si] if si < len(steps) else None
            if s is not None:
                struct.pack_into("<4BHH", b, off, s["action"], s["keycode"],
                                 s.get("modifiers", 0), 0,
                                 s.get("dmin", 0), s.get("dmax", 0))
            off += 8

    struct.pack_into("<H", b, 6, crc16(bytes(b[CFG_CRC_OFFSET:])))
    return bytes(b)


# ══════════════════════════ 设备 ══════════════════════════

class Dev:
    def __init__(self):
        self.dev = None
        self.reopen()

    def close(self):
        if self.dev is not None:
            try:
                self.dev.close()
            except Exception:
                pass
            self.dev = None

    def reopen(self, wait_s: float = 0.0) -> bool:
        """（重新）打开设备。

        设备重启或重新插拔之后，原来的 hidapi 句柄会被 Windows 作废，
        必须关掉重开 —— 句柄失效时光是"指针非空"是看不出来的。
        返回 True 表示打开了。
        """
        self.close()
        if wait_s > 0:
            time.sleep(wait_s)

        for _ in range(40):     # 最多等约 20 秒（设备重启+重新枚举需要几秒）
            cands = [d for d in hid.enumerate(VID, PID) if d["usage_page"] == USAGE_PAGE]
            if cands:
                try:
                    dev = hid.device()
                    dev.open_path(cands[0]["path"])
                    self.dev = dev
                    return True
                except Exception:
                    pass
            time.sleep(0.5)
        return False

    # ── 基本收发 ──
    def set(self, payload: bytes):
        assert len(payload) <= 63
        buf = bytearray(FRAME)
        buf[0] = RID_CFG
        buf[1:1 + len(payload)] = payload
        n = self.dev.send_feature_report(bytes(buf))
        if n < 0:
            raise RuntimeError("send_feature_report 失败")
        return n

    def get(self) -> bytes:
        buf = self.dev.get_feature_report(RID_CFG, FRAME)
        if not buf or len(buf) < FRAME:
            raise RuntimeError(f"get_feature_report 返回 {len(buf) if buf else 0} 字节")
        assert buf[0] == RID_CFG, hex(buf[0])
        return bytes(buf[1:])

    def cmd(self, cmd: int, seq: int = 0, off: int = 0, total: int = 0,
            flags: int = 0, crc: int = 0, data: bytes = b"") -> bytes:
        """发一条 SET 命令并取回应答帧"""
        p = bytearray(63)
        p[0] = cmd
        p[1] = seq
        p[2] = flags
        struct.pack_into("<HHH", p, 3, off, total, crc)
        p[9:9 + len(data)] = data
        self.set(bytes(p))
        return self.get()

    @staticmethod
    def parse_resp(r: bytes) -> dict:
        cmd, seq, flags = r[0], r[1], r[2]
        off, total, crc = struct.unpack_from("<HHH", r, 3)
        return dict(cmd=cmd, seq=seq, flags=flags, off=off, total=total,
                    crc=crc, data=r[9:])

    def _check_ack(self, r: bytes, what: str) -> dict:
        p = self.parse_resp(r)
        if p["cmd"] == CMD_NACK:
            err = p["data"][1] if len(p["data"]) > 1 else 0xFF
            raise RuntimeError(f"{what} 被拒绝: NACK err=0x{err:02X} ({ERR.get(err, '?')})")
        if p["cmd"] != CMD_ACK:
            raise RuntimeError(f"{what} 应答异常: cmd=0x{p['cmd']:02X}")
        return p

    # ── 命令 ──
    def info(self) -> dict:
        # 直接 GET（设备默认应答就是 GET_INFO）
        p = self.parse_resp(self.get())
        d = p["data"]
        return dict(proto=d[0],
                    struct_size=struct.unpack_from("<H", d, 1)[0],
                    max_payload=d[3],
                    cfg_version=struct.unpack_from("<H", d, 4)[0],
                    fw=(d[6], d[7], d[8]),
                    dirty=d[9] & 1,
                    epoch=struct.unpack_from("<H", d, 10)[0])

    def read(self) -> bytes:
        out = bytearray()
        off = 0
        while True:
            p = self.parse_resp(self.cmd(CMD_CFG_READ, off=off))
            if p["cmd"] == CMD_NACK:
                err = p["data"][1] if len(p["data"]) > 1 else 0xFF
                raise RuntimeError(f"CFG_READ 失败 err=0x{err:02X}")
            n = min(PKT_DATA_MAX, p["total"] - off)
            out += p["data"][:n]
            off += n
            if p["flags"] & 1 or off >= p["total"]:
                break
        return bytes(out)

    def write(self, cfg: bytes) -> None:
        total = len(cfg)
        seq = 0
        off = 0
        while off < total:
            n = min(PKT_DATA_MAX, total - off)
            last = (off + n >= total)
            crc = crc16(cfg) if last else 0
            r = self.cmd(CMD_CFG_WRITE, seq=seq, off=off, total=total,
                         flags=1 if last else 0, crc=crc, data=cfg[off:off + n])
            self._check_ack(r, f"CFG_WRITE seq={seq}")
            off += n
            seq += 1

    def save(self) -> dict:
        return self.parse_resp(self.cmd(CMD_CFG_SAVE))

    def reset(self) -> dict:
        return self._check_ack(self.cmd(CMD_CFG_RESET), "CFG_RESET")

    # ── 宏总开关 / 触发（0x07 已废弃 / 0x08）──
    def macro_run(self, macro_id: int) -> dict:
        """触发 / 开关宏（0xFF = 中止当前）。

        ★ 走的是固件的 macro_toggle_or_trigger()，和实体按键**完全同一条路**：
            勾了「循环执行」的宏 → 开关式：没在跑就开、正在跑就停
            非循环宏           → 每次从头跑

        返回 dict(ok, err, detail, started, busy)。
        应答布局：
            ACK : DATA[0]=1        DATA[1]=保留
                  DATA[2]=本次动作（1=已启动/触发，0=已停止）
                  DATA[3]=这一刻是否有宏在执行
            NACK: DATA[0]=原命令码  DATA[1]=错误码  DATA[2]=保留  DATA[3]=是否在执行

        ★ 判断"这次是开还是停"必须看 started（本次动作意图），
          不能看 busy —— 宏是先入队、稍后才真正启动的，刚触发那一刻 busy 仍是 0。
        """
        r = self.cmd(CMD_MACRO_RUN, data=bytes([macro_id]))
        p = self.parse_resp(r)
        d = p["data"]

        if p["cmd"] == CMD_NACK:
            err = d[1] if len(d) > 1 else 0xFF
            return dict(ok=False, err=err, started=False,
                        detail=f"NACK err=0x{err:02X} ({ERR.get(err, '?')})",
                        busy=bool(d[3]) if len(d) > 3 else False)

        return dict(ok=True, err=0, detail="已请求",
                    started=bool(d[2]) if len(d) > 2 else False,
                    busy=bool(d[3]) if len(d) > 3 else False)

    def macro_busy(self) -> bool:
        """当前是否有宏正在执行。"""
        st = self.macro_state()
        return bool(st and st["busy"])

    def macro_state(self) -> dict | None:
        """只读查询设备运行状态（KB_CMD_STATUS 0x09）。

        ★ 必须用这条只读命令，不要去读别的命令的应答来"顺便"取值 ——
          轮询一旦有副作用就不能用来做测量。
          早期版本用 0x07 的 TOGGLE 旁敲侧击读状态，而 TOGGLE 每次都会翻
          宏总开关、关掉总开关又会中止正在执行的宏，结果"测量宏跑了多久"
          变成"每 16ms 把宏掐死一次"，测出来永远是 16ms。
          （0x07 现在整条命令都已废弃。）

        为什么不用 20Hz 的 Input Report：它每 50ms 才一帧，
        做时间测量会有 50ms 的量化误差；Feature Report 往返只要 ~2ms。
        Input Report 那条路留给 GUI 做界面刷新。
        """
        r = self.cmd(CMD_STATUS)
        p = self.parse_resp(r)
        if p["cmd"] == CMD_NACK:
            return None
        d = p["data"]
        if len(d) < 16:
            return None
        return dict(
            # d[1] 原来是宏总开关状态，总开关移除后是保留字节，不再解析
            busy      = bool(d[2]),
            cur_macro = d[3],
            run_cnt   = struct.unpack_from("<I", d, 4)[0],
            abort_cnt = struct.unpack_from("<I", d, 8)[0],
            step_cnt  = struct.unpack_from("<I", d, 12)[0],
            # ★ 键盘报告的实际状态 —— 用来验证"松开之后修饰键有没有真的清掉"
            modifiers = d[16],
            pressed   = d[17],
        )

    def read_input_raw(self, timeout_ms: int = 300) -> bytes | None:
        """读一帧 Input Report（RID 0x04 之后的 32 字节负载），只读。"""
        try:
            buf = self.dev.read(64, timeout_ms)
        except Exception:
            return None
        if not buf or len(buf) < 33 or buf[0] != RID_INPUT:
            return None
        return bytes(buf[1:33])

    def reboot(self, delay_ms: int = 0) -> dict:
        """软重启设备（0x0A）—— 不需要拔插 USB。

        ⚠️ 设备不会立刻重启：固件会先把这个 ACK 发出来（本协议是"先 SET 后 GET"，
           立刻 esp_restart() 的话上位机根本取不到应答），等 delay_ms 毫秒后才真正
           esp_restart()。0 = 用固件默认 800ms。

        重启后设备会重新枚举，本对象的 hidapi 句柄随之失效，
        需要调用 reopen() 重开。
        """
        r = self.cmd(CMD_REBOOT, data=struct.pack("<H", delay_ms))
        p = self.parse_resp(r)
        if p["cmd"] == CMD_NACK:
            err = p["data"][1] if len(p["data"]) > 1 else 0xFF
            return dict(ok=False, detail=f"NACK err=0x{err:02X} ({ERR.get(err, '?')})")

        d = p["data"]
        dly = struct.unpack_from("<H", d, 1)[0] if len(d) >= 3 else 0
        flushed = bool(d[3]) if len(d) > 3 else False
        return dict(ok=True, delay_ms=dly, flushed=flushed,
                    detail=f"设备将在 {dly}ms 后重启（落盘={'OK' if flushed else '失败'}）")


# ══════════════════════════ CLI ══════════════════════════

def first_diff(a: bytes, b: bytes) -> str:
    for i in range(min(len(a), len(b))):
        if a[i] != b[i]:
            return f"首个差异在偏移 {i}: 重启前 0x{a[i]:02X} / 重启后 0x{b[i]:02X}"
    return f"长度不同 {len(a)} vs {len(b)}"


def fmt_cfg(c: dict) -> str:
    b = c["buttons"]
    out = [
        f"  magic=0x{c['magic']:04X} version=0x{c['version']:04X} "
        f"size={c['size']} crc=0x{c['crc16']:04X}",
        f"  摇杆      : 左 死区{c['sticks'][0]['deadzone']}% 反向{c['sticks'][0]['invert_x']}/{c['sticks'][0]['invert_y']}"
        f"   右 死区{c['sticks'][1]['deadzone']}% 反向{c['sticks'][1]['invert_x']}/{c['sticks'][1]['invert_y']}",
        # ★ 0x0004 起摇杆方向键就是普通槽位（左摇杆 = 16/17/18/19）
        f"  左摇杆方向: 上=0x{c['buttons'][16]['keycode']:02X} 下=0x{c['buttons'][17]['keycode']:02X} "
        f"左=0x{c['buttons'][18]['keycode']:02X} 右=0x{c['buttons'][19]['keycode']:02X}",
        f"  右摇杆方向: 上=0x{c['buttons'][20]['keycode']:02X} 下=0x{c['buttons'][21]['keycode']:02X} "
        f"左=0x{c['buttons'][22]['keycode']:02X} 右=0x{c['buttons'][23]['keycode']:02X}",
    ]

    for i, x in enumerate(b):
        macro = f"  宏#{x['macro_id']}" if (x['flags'] & BTNFLAG_HAS_MACRO) else ""
        en = "✔" if (x['flags'] & BTNFLAG_ENABLED) else "✗"
        out.append(f"  槽位{i:2d} {SLOT_NAMES[i]:<8}: {en} 键码=0x{x['keycode']:02X}{macro}")

    out.append(f"  宏数量    : {c['macro_count']}")
    for i, m in enumerate(c.get("macros", [])[:c["macro_count"]]):
        loop = "  ★循环（触发按钮=开/停切换）" if m["flags"] & MACRO_FLAG_LOOP else ""
        out.append(f"    宏#{i}: {m['step_count']} 步  超时={m['timeout'] or 30000}ms{loop}")
        for j, s in enumerate(m["steps"][:m["step_count"]]):
            name = ACT_NAMES.get(s["action"], f"?{s['action']}")
            if s["action"] in (ACT_KEY_TAP, ACT_COMBO, ACT_KEY_DOWN, ACT_KEY_UP):
                out.append(f"      {j}. {name} 键码=0x{s['keycode']:02X} mod=0x{s['modifiers']:02X}")
            elif s["action"] == ACT_RAND_DELAY:
                # 随机延迟：dmin = 基准值(ms)，dmax = 抖动(%)
                lo, hi = rand_bounds(s["dmin"], s["dmax"])
                out.append(f"      {j}. {name} 基准={s['dmin']}ms 抖动=±{s['dmax']}%  "
                           f"→ 实际范围 [{lo},{hi}]ms")
            else:
                out.append(f"      {j}. {name} {s['dmin']}ms")
    return "\n".join(out)


def rand_bounds(base_ms: int, jitter_pct: int) -> tuple[int, int]:
    """复刻固件 rand_gauss_base() 的取值范围，方便核对界面/固件理解一致。

    固件：均值 = base，sigma = base*jitter/100/2，截断到 [base-j, base+j]。
    """
    if base_ms <= 0:
        base_ms = 100
    j = min(jitter_pct, MAX_JITTER_PCT)
    if j == 0:
        return base_ms, base_ms
    spread = base_ms * j // 100
    if spread == 0:
        return base_ms, base_ms
    return max(1, base_ms - spread), base_ms + spread


def empty_macro() -> dict:
    """一个空的宏槽 —— step_count=0 表示"这个宏不存在"。"""
    return dict(step_count=0, flags=0, timeout=0,
                steps=[dict(action=ACT_DELAY, keycode=0, modifiers=0, dmin=0, dmax=0)
                       for _ in range(MACRO_STEP_MAX)])


def build_demo_macro(cfg: dict) -> dict:
    """挂两个演示宏，一次把两个新功能都覆盖：

      宏#0（按钮1，不循环）：敲 A → 随机延迟(基准200ms ±30%) → 敲 B → 随机延迟
                            验证「基准值 + 抖动%」的随机延迟
      宏#1（按钮2，★循环）：敲 C → 固定延迟 400ms，勾了循环
                            → 按一下按钮2 开始连续敲 C，再按一下停止

    ★ 宏没有总开关了（功能已移除）：上电即可用，不需要额外开关。
      循环宏的"停"就是再按一次它的触发按钮。

    ★ 这里会把没用到的宏槽（#2、#3）显式清空。
      踩过的坑：原实现是 `list(cfg["macros"])` 直接沿用旧值，
      于是之前测试跑出来的宏#3/#4 残留着定义，而 macro_count 又写着 2 ——
      两者不一致，上位机一读一存就把计数抬上去，回读字节当然对不上。
      所以清空必须显式做，不能指望"反正没人用"。
    """
    cfg = dict(cfg)
    cfg["macro_count"] = 2

    def pad(steps):
        steps = list(steps)
        while len(steps) < MACRO_STEP_MAX:
            steps.append(dict(action=ACT_DELAY, keycode=0, modifiers=0, dmin=0, dmax=0))
        return steps

    # 宏#0：A → 随机延迟(基准 200ms, 抖动 30%) → B → 随机延迟(基准 300ms, 抖动 60%)
    m0 = pad([
        dict(action=ACT_KEY_TAP,    keycode=0x04, modifiers=0, dmin=0,   dmax=0),
        dict(action=ACT_RAND_DELAY, keycode=0,    modifiers=0, dmin=200, dmax=30),
        dict(action=ACT_KEY_TAP,    keycode=0x05, modifiers=0, dmin=0,   dmax=0),
        dict(action=ACT_RAND_DELAY, keycode=0,    modifiers=0, dmin=300, dmax=60),
    ])

    # 宏#1：循环敲 C（每 400ms 一次）
    m1 = pad([
        dict(action=ACT_KEY_TAP, keycode=0x06, modifiers=0, dmin=0,   dmax=0),
        dict(action=ACT_DELAY,   keycode=0,    modifiers=0, dmin=400, dmax=0),
    ])

    # 四个槽全部重建：用到的填内容，没用到的一律清空
    cfg["macros"] = [
        dict(step_count=4, flags=0,               timeout=10000, steps=m0),   # 不循环
        dict(step_count=2, flags=MACRO_FLAG_LOOP, timeout=10000, steps=m1),   # ★ 循环
        empty_macro(),
        empty_macro(),
    ]

    btns = [dict(x) for x in cfg["buttons"]]
    btns[0]["flags"] = BTNFLAG_ENABLED | BTNFLAG_HAS_MACRO
    btns[0]["macro_id"] = 0
    btns[1]["flags"] = BTNFLAG_ENABLED | BTNFLAG_HAS_MACRO
    btns[1]["macro_id"] = 1
    # 清掉可能残留的其它宏绑定（比如上次测试把按钮3绑到宏#2）
    for i in range(2, BTN_COUNT):
        btns[i]["flags"] = BTNFLAG_ENABLED
        btns[i]["macro_id"] = 0xFF
    cfg["buttons"] = btns
    return cfg


def main() -> int:
    ap = argparse.ArgumentParser(description="配置通道协议工具")
    sub = ap.add_subparsers(dest="action", required=True)
    sub.add_parser("info")
    sub.add_parser("read")
    sub.add_parser("save")
    sub.add_parser("reset")
    sub.add_parser("roundtrip")
    sub.add_parser("demo-macro")
    sr = sub.add_parser("reboot", help="软重启设备（不必拔插 USB）")
    sr.add_argument("--delay", type=int, default=0,
                    help="重启延迟毫秒（0 = 固件默认 800；太小上位机可能来不及取应答）")
    sr.add_argument("--verify", action="store_true",
                    help="重启后等设备回来并回读配置，与重启前比对（验证 NVS 持久化）")

    sp = sub.add_parser("set")
    sp.add_argument("--deadzone", type=int)
    for d in DIRS:
        sp.add_argument(f"--{d}", type=lambda s: int(s, 0))
    for i in range(BTN_COUNT):
        sp.add_argument(f"--btn{i+1}", type=lambda s: int(s, 0))
    sp.add_argument("--save", action="store_true", help="改完立即保存")
    args = ap.parse_args()

    selfcheck_crc()
    dev = Dev()
    try:
        if args.action == "info":
            i = dev.info()
            print("设备信息:")
            print(f"  协议版本  : 0x{i['proto']:02X}")
            print(f"  配置结构  : {i['struct_size']} 字节（本地 {CFG_SIZE}）"
                  f"  {'✔ 一致' if i['struct_size'] == CFG_SIZE else '✗ 不一致！'}")
            print(f"  单包负载  : {i['max_payload']} 字节")
            print(f"  配置格式  : 0x{i['cfg_version']:04X}")
            print(f"  固件版本  : {i['fw'][0]}.{i['fw'][1]}.{i['fw'][2]}")
            print(f"  未落盘    : {i['dirty']}   epoch={i['epoch']}")

        elif args.action == "read":
            raw = dev.read()
            c = unpack_cfg(raw)
            print(f"读回 {len(raw)} 字节")
            print(fmt_cfg(c))
            calc = crc16(raw[CFG_CRC_OFFSET:])
            print(f"\n  结构 CRC : 0x{c['crc16']:04X} / 算出 0x{calc:04X}  "
                  f"{'✔' if calc == c['crc16'] else '✗'}")

        elif args.action == "set":
            c = unpack_cfg(dev.read())
            changed = []
            if args.deadzone is not None:
                # ★ 0x0004 起死区按摇杆分开；--deadzone 改左摇杆（要改右摇杆用 --deadzone-r）
                c["sticks"][0]["deadzone"] = args.deadzone
                changed.append(f"左摇杆死区={args.deadzone}")
            for d in DIRS:
                v = getattr(args, d)
                if v is not None:
                    # ★ 0x0004 起摇杆方向键就是普通槽位：左摇杆 ↑↓←→ = 槽位 16/17/18/19
                    slot = {"up": 16, "down": 17, "left": 18, "right": 19}[d]
                    c["buttons"][slot]["keycode"] = v
                    changed.append(f"左摇杆{d}=0x{v:02X}（槽位{slot}）")
            for i in range(BTN_COUNT):
                v = getattr(args, f"btn{i+1}")
                if v is not None:
                    c["buttons"][i]["keycode"] = v
                    changed.append(f"槽位{i}（{SLOT_NAMES[i]}）=0x{v:02X}")

            if not changed:
                print("没有指定要改的项。")
                return 1

            dev.write(pack_cfg(c))
            print("已写入暂存区: " + ", ".join(changed))
            if args.save:
                dev.save()
                print("已发送 CFG_SAVE（提交 + 落盘）")
                raw2 = dev.read()
                print("回读验证: " + (fmt_cfg(unpack_cfg(raw2))))

        elif args.action == "demo-macro":
            c = unpack_cfg(dev.read())
            c2 = build_demo_macro(c)
            dev.write(pack_cfg(c2))
            dev.save()
            print("✔ 已安装演示宏并保存：")
            print("  宏#0（按钮1 / GPIO41，不循环）：敲 A → 随机延迟(基准200ms ±30%) → 敲 B")
            print("  宏#1（按钮2 / GPIO40，★循环）   ：敲 C → 固定 400ms，无限重复")
            print("  宏总开关：已移除 —— 宏上电即可用，不需要额外开关")
            print()
            print("  验证方法：打开记事本")
            print("    按按钮1 → 打出 a、b（间隔随机，约 140~260ms）")
            print("    按按钮2 → 开始连续打出 c")
            print("    再按按钮2 → ★ 停止（同一个按钮开也关）")
            print("              （或等总超时 10000ms 自动收尾）")
            print()
            print("  回读确认：")
            print(fmt_cfg(unpack_cfg(dev.read())))

        elif args.action == "reboot":
            before = dev.read()
            r = dev.reboot(args.delay)
            if not r["ok"]:
                print(f"✗ 重启命令被拒：{r['detail']}")
                return 1
            print(f"✔ {r['detail']}")
            print("  设备不会立刻重启：固件要先把这个应答发出来（先 SET 后 GET），")
            print("  所以它等了一会儿才真正 esp_restart()。")

            if args.verify:
                print("\n  等待设备重启并重新枚举…")
                t0 = time.time()
                if not dev.reopen():
                    print("✗ 等不到设备回来（超过约 20 秒）")
                    return 1
                print(f"  ✔ 已重新打开（等待 {time.time() - t0:.1f}s）")

                # 重启后设备刚起来，稍等一下再读
                time.sleep(1.0)
                after = dev.read()
                same = after == before
                print(f"  [{'PASS' if same else 'FAIL'}] 重启后配置与重启前逐字节一致"
                      f"（{'396 字节完全相同' if same else first_diff(before, after)}）")
                print(fmt_cfg(unpack_cfg(after)))
                return 0 if same else 1
            else:
                print("\n  提示：加了 --verify 会在重启后自动等设备回来并回读比对。")

        elif args.action == "save":
            r = dev.save()
            p = Dev.parse_resp(r)
            if p["cmd"] == CMD_NACK:
                print(f"保存被拒: {ERR.get(p['data'][1], '?')}")
            else:
                remain = struct.unpack_from("<H", p["data"], 2)[0]
                err = p["data"][1]
                print(f"已生效；落盘: {'推迟 ' + str(remain) + ' ms（频率限制）' if err else '立即进行'}")

        elif args.action == "reset":
            dev.reset()
            print("已恢复出厂默认")
            print(fmt_cfg(unpack_cfg(dev.read())))

        elif args.action == "roundtrip":
            print("=== 端到端自测 ===")
            i = dev.info()
            ok = i["struct_size"] == CFG_SIZE
            print(f"  [{'PASS' if ok else 'FAIL'}] GET_INFO 结构体大小一致 ({i['struct_size']})")

            orig = dev.read()
            c0 = unpack_cfg(orig)
            print(f"  [PASS] CFG_READ 读回 {len(orig)} 字节，CRC 0x{c0['crc16']:04X}")

            # 改一个槽位的键码再存（0x0004 起死区按摇杆分开，用槽位更直观）
            SLOT = 5                       # 槽位 5 = RB
            old_code = c0["buttons"][SLOT]["keycode"]
            new_code = 0x2C if old_code != 0x2C else 0x2D
            c0["buttons"][SLOT]["keycode"] = new_code
            dev.write(pack_cfg(c0))
            dev.save()

            again = unpack_cfg(dev.read())
            got = again["buttons"][SLOT]["keycode"]
            ok2 = got == new_code
            print(f"  [{'PASS' if ok2 else 'FAIL'}] 改槽位{SLOT}（{SLOT_NAMES[SLOT]}）"
                  f"键码 0x{new_code:02X} → 回读 0x{got:02X}")

            # 改回原值
            again["buttons"][SLOT]["keycode"] = old_code
            print("  (已改回，可在固件端断电上电验证 NVS 持久化)")
            return 0 if (ok and ok2) else 1

    finally:
        dev.close()
    return 0


if __name__ == "__main__":
    # Windows 控制台默认是 GBK，打印 ✔/→ 这类字符会 UnicodeEncodeError。
    # 强制 stdout 用 UTF-8（errors="replace" 兜底，绝不因为一个字符中断流程）。
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    sys.exit(main())
