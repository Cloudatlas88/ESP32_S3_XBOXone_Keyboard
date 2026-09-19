"""跨语言一致性检查：固件 / 上位机 / Python 工具里的协议常量必须完全对得上。

为什么需要：
  这份协议的常量散在三种语言里 ——
    固件   app_config.h / kb_config.h（C 宏 + slot_idx_t 枚举顺序）
    上位机 KbConfig.cs / SlotMap.cs（C# 常量 + 名字表）
    工具   cfg_proto.py（Python 常量 + 名字表）
  它们**没有任何编译器帮忙对齐**。而实测每一样都漂移过：
    · 槽位显示名两套叫法（宏触发绑定叫 "LB"，布局页叫 "LB 肩键"）
    · 配置版本/尺寸漏改（0x0004→0x0005→0x0006 各漏过一次）
    · 帧偏移漏改（read_input.py 把 axis_fault 当成原始电平低字节）
  每次现象都很远（"设备起不来"/"CFG_READ 无响应"/"扳机不亮"），
  查起来要绕一大圈。所以做成一条命令：**改完跑一下**。

用法：python tools/check_consistency.py
退出码 0 = 一致，1 = 有分歧。
"""
import io
import os
import re
import sys

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..')
FW = os.path.join(ROOT, 'firmware', 'main_fw', 'main')
HOST = os.path.join(ROOT, 'host', 'KbConfigurator')
TOOLS = os.path.join(ROOT, 'tools')

problems = []
oks = []


def ok(msg):
    oks.append(msg)


def bad(msg):
    problems.append(msg)


def read(*parts):
    return io.open(os.path.join(*parts), encoding='utf-8').read()


# ══════════ 1. 槽位数量 ══════════
fw_app = read(FW, 'app_config.h')
fw_slots = int(re.search(r'#define\s+SLOT_COUNT\s+(\d+)', fw_app).group(1))

host_cfg = read(HOST, 'Protocol', 'KbConfig.cs')
host_slots = int(re.search(r'const int SlotCount\s*=\s*(\d+)', host_cfg).group(1))

py = read(TOOLS, 'cfg_proto.py')
py_slots = int(re.search(r'^BTN_COUNT\s*=\s*(\d+)', py, re.M).group(1))

if fw_slots == host_slots == py_slots:
    ok(f'槽位数量三方一致：{fw_slots}')
else:
    bad(f'槽位数量不一致：固件 {fw_slots} / 上位机 {host_slots} / 工具 {py_slots}')

# ══════════ 2. 槽位命中数（枚举 vs 名字表）══════════
enum_body = fw_app[fw_app.index('typedef enum {'):fw_app.index('} slot_idx_t;')]
fw_enum_names = [n.strip() for n in re.findall(r'SLOT_(\w+)[,\s]', enum_body)]
fw_enum_names = [n for n in fw_enum_names if n != 'MAX']

cs_map = read(HOST, 'Protocol', 'SlotMap.cs')
block = cs_map[cs_map.index('public static readonly string[] SlotNames'):]
cs_names = re.findall(r'"([^"]+)"', block[:block.index('};')])

py_names_block = py[py.index('SLOT_NAMES = ['):]
py_names = re.findall(r'"([^"]+)"', py_names_block[:py_names_block.index(']')])

if len(fw_enum_names) == len(cs_names) == len(py_names):
    ok(f'槽位名字表项数一致：{len(cs_names)}（固件枚举 / C# / Python）')
else:
    bad(f'槽位名字表项数不一致：固件枚举 {len(fw_enum_names)} / C# {len(cs_names)} / Python {len(py_names)}')

if cs_names == py_names:
    ok('槽位显示名 C# 与 Python 逐字一致')
else:
    diff = [f'{i}: {a!r}≠{b!r}' for i, (a, b) in enumerate(zip(cs_names, py_names)) if a != b]
    bad('槽位显示名不一致 → ' + '; '.join(diff[:4]))

# ══════════ 3. 配置版本与尺寸 ══════════
fw_ver = int(re.search(r'#define\s+KB_CFG_VERSION\s+(0x[0-9A-Fa-f]+)', read(FW, 'kb_config.h')).group(1), 16)
host_ver = int(re.search(r'const ushort Version\s*=\s*(0x[0-9A-Fa-f]+)', host_cfg).group(1), 16)
py_ver = int(re.search(r'^CFG_VERSION\s*=\s*(0x[0-9A-Fa-f]+)', py, re.M).group(1), 16)

if fw_ver == host_ver == py_ver:
    ok(f'配置版本三方一致：0x{fw_ver:04X}')
else:
    bad(f'配置版本不一致：固件 0x{fw_ver:04X} / 上位机 0x{host_ver:04X} / 工具 0x{py_ver:04X}')

fw_size = int(re.search(r'sizeof\(kb_config_t\)\s*==\s*(\d+)', read(FW, 'kb_config.h')).group(1))
host_size = int(re.search(r'const int\s+Size\s*=\s*(\d+)', host_cfg).group(1))
py_size = int(re.search(r'^CFG_SIZE\s*=\s*(\d+)', py, re.M).group(1))

if fw_size == host_size == py_size:
    ok(f'配置尺寸三方一致：{fw_size} 字节')
else:
    bad(f'配置尺寸不一致：固件 {fw_size} / 上位机 {host_size} / 工具 {py_size}')


# ══════════ 4. 尺寸必须能从各段算出来 ══════════
# 8(头) + stick×8 + slots×8 + 4(macro_count) + macros×(4+steps×8) + 64(保留)
fw_slots2 = fw_slots
sticks = int(re.search(r'#define\s+STICK_COUNT\s+(\d+)', fw_app).group(1))
macros = int(re.search(r'#define\s+MACRO_MAX\s+(\d+)', read(FW, 'kb_config.h')).group(1))
steps = int(re.search(r'#define\s+MACRO_STEP_MAX\s+(\d+)', read(FW, 'kb_config.h')).group(1))
calc = 8 + sticks * 8 + fw_slots2 * 8 + 4 + macros * (4 + steps * 8) + 64

if calc == fw_size:
    ok(f'配置尺寸可由各段算出：8+{sticks}×8+{fw_slots2}×8+4+{macros}×(4+{steps}×8)+64 = {calc}')
else:
    bad(f'配置尺寸算不出来：按各段算是 {calc}，而断言写的是 {fw_size}')

# ══════════ 5. 上报帧尺寸 ══════════
fw_payload = int(re.search(r'#define\s+KB_INPUT_PAYLOAD\s+(\d+)', fw_app).group(1))
host_payload = int(re.search(r'const int PayloadSize\s*=\s*(\d+)',
                             read(HOST, 'Protocol', 'InputState.cs')).group(1))
if fw_payload == host_payload:
    ok(f'上报帧长度一致：{fw_payload} 字节')
else:
    bad(f'上报帧长度不一致：固件 {fw_payload} / 上位机 {host_payload}')

# ══════════ 6. 上报帧偏移表（固件 vs 上位机解析）══════════
off_names = ['IN_OFF_FLAGS', 'IN_OFF_BUTTONS', 'IN_OFF_DIRS_DPAD',
             'IN_OFF_AXIS_FAULT', 'IN_OFF_BTN_RAW', 'IN_OFF_TRAVEL']
fw_offs = {n: int(re.search(rf'#define\s+{n}\s+(\d+)', fw_app).group(1)) for n in off_names}

istate = read(HOST, 'Protocol', 'InputState.cs')
# 上位机是硬编码偏移；从注释表和解析代码里抽取需要的一致性证据：
# 解析里 slots 用 p[1..]，dirs 用 p[5..7]，btn_raw 用 p[34..37]，行程用 38+
checks = [
    ('slots', fw_offs['IN_OFF_BUTTONS'], r'Slots\s*=\s*\(uint\)\(p\[(\d+)\]'),
    ('dirs', fw_offs['IN_OFF_DIRS_DPAD'], r'DirsDpad\s*=\s*p\[(\d+)\]'),
    ('axis_fault', fw_offs['IN_OFF_AXIS_FAULT'], r'AxisFault\s*=\s*p\[(\d+)\]'),
    ('btn_raw', fw_offs['IN_OFF_BTN_RAW'], r'BtnRaw\s*=\s*p\.Length > \d+ \? new\[\] \{ p\[(\d+)\]'),
]
mismatch = []
for label, fw_off, pat in checks:
    m = re.search(pat, istate)
    if not m:
        mismatch.append(f'{label}: 上位机解析里找不到对应式子')
        continue
    if int(m.group(1)) != fw_off:
        mismatch.append(f'{label}: 固件 {fw_off} ≠ 上位机 {m.group(1)}')

m = re.search(r'int ro = (\d+) \+ a \* 2', istate)
if m and int(m.group(1)) != int(re.search(r'#define\s+IN_OFF_RAW\s+(\d+)', fw_app).group(1)):
    mismatch.append(f'raw: 固件 ≠ 上位机 {m.group(1)}')

if not mismatch:
    ok('上报帧偏移表：上位机解析与固件 IN_OFF_* 对得上')
else:
    bad('上报帧偏移不一致 → ' + '; '.join(mismatch))

# ══════════ 7. 工具里的帧偏移（read_input.py）══════════
ri = read(TOOLS, 'read_input.py')
ri_map = {
    'OFF_SLOTS': 'IN_OFF_BUTTONS',
    'OFF_DIRS': 'IN_OFF_DIRS_DPAD',
    'OFF_AXIS_FAULT': 'IN_OFF_AXIS_FAULT',
    'OFF_BTN_RAW': 'IN_OFF_BTN_RAW',
}
ri_bad = []
for ri_key, fw_key in ri_map.items():
    m = re.search(rf'^{ri_key}\s*=\s*(\d+)', ri, re.M)
    if not m:
        ri_bad.append(f'{ri_key}: 找不到')
    elif int(m.group(1)) != fw_offs[fw_key]:
        ri_bad.append(f'{ri_key}={m.group(1)} 固件 {fw_key}={fw_offs[fw_key]}')
if not ri_bad:
    ok('read_input.py 的帧偏移与固件一致')
else:
    bad('read_input.py 帧偏移不一致 → ' + '; '.join(ri_bad))

# ══════════ 8. 宏步数上限 ══════════
py_steps = int(re.search(r'^MACRO_STEP_MAX\s*=\s*(\d+)', py, re.M).group(1))
host_steps = int(re.search(r'const int\s+MacroStepMax\s*=\s*(\d+)', host_cfg).group(1))
if steps == py_steps == host_steps:
    ok(f'宏步数上限三方一致：{steps}')
else:
    bad(f'宏步数上限不一致：固件 {steps} / 上位机 {host_steps} / 工具 {py_steps}')

# ══════════ 汇总 ══════════
print('═' * 62)
print('  跨语言协议常量一致性检查')
print('═' * 62)
for m in oks:
    print(f'  [PASS] {m}')
if problems:
    print()
    for m in problems:
        print(f'  [FAIL] {m}')
    print(f'\n  结果：{len(problems)} 处分歧（{len(oks)} 项通过）')
    sys.exit(1)

print(f'\n  结果：{len(oks)} 项全部一致')
