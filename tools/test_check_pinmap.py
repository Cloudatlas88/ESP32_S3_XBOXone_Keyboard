"""
Self-test for tools/check_pinmap.py.

A validator that never fails is worthless, so this injects known-bad pin maps
and asserts the checker actually rejects each one, plus asserts the good map
passes. Run: python tools/test_check_pinmap.py
"""
import csv
import io
import os
import subprocess
import sys
import tempfile

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GOOD = os.path.join(ROOT, 'hardware_ref', 'xbox_pcb', 'pinmap.csv')
CHECKER = os.path.join(ROOT, 'tools', 'check_pinmap.py')

src = open(GOOD, encoding='utf-8').read()

CASES = [
    ('GPIO37 = Octal PSRAM 禁用脚', '5,数字,A,6,', '5,数字,A,37,', '禁用脚'),
    ('GPIO6 被重复分配', '6,数字,B,7,-', '6,数字,B,6,-', '重复分配'),
    ('ADC 通道写错 (CH1 -> CH9)', '2,模拟,左摇杆 Y,2,ADC1_CH1', '2,模拟,左摇杆 Y,2,ADC1_CH9', 'ADC 通道写错'),
    # GPIO6 本来就是 ADC1_CH5，所以下面这条是【正例】：标对了不该报错
    ('GPIO6 标 ADC1_CH5（正确，应通过）', '5,数字,A,6,-', '5,数字,A,6,ADC1_CH5', None),
    # GPIO38 根本不是 ADC 引脚，标成 ADC 必须报错
    ('GPIO38 标成 ADC 脚（非 ADC 脚）', '19,数字,十字键 左,38,-', '19,数字,十字键 左,38,ADC1_CH0', '不是 ADC1 引脚'),
    ('GPIO19 = 原生 USB D-', '13,数字,Xbox,14,-', '13,数字,Xbox,19,-', '禁用脚'),
]


def run(csv_path):
    """Run the checker against a specific CSV, returning (exit_code, output)."""
    p = subprocess.run([sys.executable, CHECKER, csv_path], capture_output=True,
                       text=True, encoding='utf-8', errors='replace')
    return p.returncode, (p.stdout or '') + (p.stderr or '')


print('=' * 70)
print('check_pinmap 自检')
print('=' * 70)

fails = 0

# ── 正例：好表必须通过 ──
rc, out = run(GOOD)
ok = rc == 0 and '校验通过' in out
print(f'\n[正例] 当前引脚表应通过 ................ {"PASS" if ok else "FAIL"}')
if not ok:
    fails += 1
    print(f'  exit={rc}\n{out[-600:]}')

# ── 注入用例：expect=None 表示「改动是合法的，必须通过」 ──
for name, old, new, expect in CASES:
    if old not in src:
        print(f'\n[用例] {name} ... SKIP（找不到锚点 {old!r}，引脚表可能改过了）')
        continue
    bad = src.replace(old, new, 1)
    with tempfile.NamedTemporaryFile('w', suffix='.csv', delete=False,
                                     encoding='utf-8', newline='') as f:
        f.write(bad)
        path = f.name
    try:
        rc, out = run(path)
        if expect is None:
            ok = rc == 0
            kind = '正例'
        else:
            ok = rc != 0 and expect in out
            kind = '反例'
        print(f'\n[{kind}] {name:<34} {"PASS" if ok else "FAIL"}')
        if not ok:
            fails += 1
            if expect is None:
                print(f'  期望 exit=0，实际 exit={rc}')
            else:
                print(f'  期望 exit!=0 且含「{expect}」，实际 exit={rc}')
            print('  ' + out[-500:].replace('\n', '\n  '))
    finally:
        os.unlink(path)

print()
print('=' * 70)
if fails:
    print(f'[X] {fails} 项未通过')
    sys.exit(1)
n_bad = sum(1 for c in CASES if c[3] is not None)
print(f'[OK] 全部通过（2 正例 + {n_bad} 反例）—— 校验器确实有效')
