"""
Validate hardware_ref/xbox_pcb/pinmap.csv.

Catches the mistakes that are expensive to find after soldering:
  - the same GPIO assigned twice
  - a pin used that is unusable on ESP32-S3 N16R8 (PSRAM / flash / USB / UART / strapping)
  - an ADC channel that does not match the GPIO on ESP32-S3
  - a digital pin that is not input-capable
Exit code 0 = OK, 1 = problems found.

Usage: python tools/check_pinmap.py [path/to/pinmap.csv]
"""

import csv
import os
import sys
from collections import defaultdict

# The Windows console here is GBK; force UTF-8 so Chinese + markers don't blow up
try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

_DEFAULT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                        'hardware_ref', 'xbox_pcb', 'pinmap.csv')
CSV = sys.argv[1] if len(sys.argv) > 1 else _DEFAULT

# GPIO -> ADC1 channel on ESP32-S3 (ADC2 starts at GPIO11, we deliberately avoid it)
ADC1 = {1: 0, 2: 1, 3: 2, 4: 3, 5: 4, 6: 5, 7: 6, 8: 7, 9: 8, 10: 9}

FORBIDDEN = {
    0:  'BOOT 按键（固件已占用）',
    3:  'strapping 脚（JTAG 源选择）',
    19: '原生 USB D-',
    20: '原生 USB D+',
    26: '内置 Flash', 27: '内置 Flash', 28: '内置 Flash', 29: '内置 Flash',
    30: '内置 Flash', 31: '内置 Flash', 32: '内置 Flash',
    33: 'Octal PSRAM', 34: 'Octal PSRAM', 35: 'Octal PSRAM',
    36: 'Octal PSRAM', 37: 'Octal PSRAM',
    43: 'UART0 TX（烧录/日志）',
    44: 'UART0 RX（烧录/日志）',
    45: 'strapping 脚',
    46: 'strapping 脚',
}

USABLE = (set(range(1, 19)) | {21} | set(range(38, 43)) | {47, 48}) - set(FORBIDDEN)

rows = list(csv.DictReader(open(CSV, encoding='utf-8')))
problems = []
used = defaultdict(list)      # GPIO -> [names]  (all assigned rows, incl. 保留)
reserved = []                 # GPIOs sitting in the 保留 category

print(f'{CSV}: {len(rows)} 行\n')
print(f'{"#":>3}  {"类别":<4} {"功能":<14} {"GPIO":>4}  {"ADC":<10} 结果')
print('-' * 62)

for r in rows:
    gpio_s = r['gpio'].strip()
    if gpio_s in ('3V3', 'GND', 'GND', '-'):
        continue
    gpio = int(gpio_s)
    used[gpio].append(r['name'])
    if r['category'].strip() == '保留':
        reserved.append(gpio)
    label = r['adc_channel'].strip()
    status = 'OK'

    if gpio in FORBIDDEN:
        problems.append(f'GPIO{gpio}（{r["name"]}）不可用：{FORBIDDEN[gpio]}')
        status = f'[X] 禁用脚 {FORBIDDEN[gpio]}'
    elif gpio not in USABLE:
        problems.append(f'GPIO{gpio}（{r["name"]}）不在可用引脚池内')
        status = '[X] 不在可用池'

    if label.startswith('ADC'):
        if gpio not in ADC1:
            problems.append(f'GPIO{gpio}（{r["name"]}）不是 ADC1 引脚，却标了 {label}')
            status = '[X] 非 ADC1 脚'
        else:
            want = f'ADC1_CH{ADC1[gpio]}'
            if label != want:
                problems.append(f'GPIO{gpio}（{r["name"]}）ADC 通道写错：CSV={label} 实际={want}')
                status = f'[X] 应为 {want}'
            else:
                status = f'OK ({want})'

    print(f'{r["num"]:>3}  {r["category"]:<4} {r["name"]:<14} {gpio:>4}  {label:<10} {status}')

dups = {g: n for g, n in used.items() if len(n) > 1}
for g, names in sorted(dups.items()):
    problems.append(f'GPIO{g} 被重复分配：{names}')

active = sorted(set(used) - set(reserved))
print()
print('-' * 62)
print(f'实际接线占用 {len(active)} 个 GPIO：{active}')
print(f'保留不接   {len(reserved)} 个 GPIO：{sorted(reserved)}')
print(f'完全空闲   {len(USABLE - set(used))} 个 GPIO：{sorted(USABLE - set(used))}')

if problems:
    print(f'\n[X] 发现 {len(problems)} 个问题：')
    for p in problems:
        print(f'  - {p}')
    sys.exit(1)

print('\n[OK] 引脚表校验通过：无重复、无禁用脚、ADC 通道正确')
