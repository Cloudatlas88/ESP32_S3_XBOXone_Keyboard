"""清理后的收尾核验：语法 + 悬空引用 + 目录结构。

★ 为什么要专门跑一遍：这次改的全是**注释和文档字符串**，
  这类改动平时不会被执行到 —— 但不小心吃掉一个注释结束符
  就是语法错误，等到下次真要用脚本时才发现
  （那时候已经想不起来是哪次改的了）。
  （讽刺的是，本脚本第一版就在自己的文档字符串里写了三个引号，
    把自己提前结束了 —— 正好印证了这条。）
"""
import ast
import io
import os
import re
import sys

sys.stdout.reconfigure(encoding='utf-8', errors='replace')
ROOT = r'D:\deepseek\ShouBing'
bad = 0

# ── 1. 所有 Python 工具语法 ──
print('【1】tools/*.py 语法')
for f in sorted(os.listdir(os.path.join(ROOT, 'tools'))):
    if not f.endswith('.py'):
        continue
    p = os.path.join(ROOT, 'tools', f)
    try:
        ast.parse(io.open(p, encoding='utf-8').read())
        print(f'  [OK]   {f}')
    except SyntaxError as e:
        print(f'  [FAIL] {f}: {e}')
        bad += 1

# ── 2. 项目结构.md 里有没有指向已不存在文件的 .md 引用 ──
print('\n【2】项目结构.md 的 .md 引用')
doc = io.open(os.path.join(ROOT, '项目结构.md'), encoding='utf-8').read()
refs = set(re.findall(r'[\w\u4e00-\u9fa5_]+\.md', doc))
for r in sorted(refs):
    if r == '项目结构.md':
        continue
    # FLASH_LOG.md 会在烧录时自动重建，属于预期提到
    ok = (r == 'FLASH_LOG.md')
    print(f'  [{"OK" if ok else "??"}]   {r}' + ('' if ok else '  ← 这个文件已删，确认是历史说明而非引用'))
    if not ok:
        bad += 1

# ── 3. 目录里确实只剩一个 md（排除第三方与归档）──
print('\n【3】剩余 .md')
left = []
for dp, dns, fns in os.walk(ROOT):
    dns[:] = [d for d in dns if d not in ('.git', 'managed_components', 'flash_archive')]
    for f in fns:
        if f.endswith('.md'):
            left.append(os.path.relpath(os.path.join(dp, f), ROOT))
for f in sorted(left):
    print(f'  {f}')
if left != ['项目结构.md']:
    print(f'  [FAIL] 期望只剩 项目结构.md')
    bad += 1
else:
    print('  [OK]   只剩 项目结构.md')

# ── 4. 关键文件仍在 ──
print('\n【4】关键文件')
need = ['release/Ver001/S3_Keyboard.exe',
        'release/Ver001/firmware/S3_Keyboard_Ver001.bin',
        'release/Ver001/flash.cmd',
        'firmware/main_fw/main/app_config.h',
        'firmware/main_fw/main/main.c',
        'firmware/main_fw/CMakeLists.txt',
        'host/KbConfigurator/KbConfigurator.csproj',
        'host/KbConfigurator/Assets/layout.json',
        'host/native/hidapi-x64.dll',
        'tools/make_release.py',
        'hardware_ref/xbox_pcb/pinmap.csv',
        '项目结构.md']
for r in need:
    p = os.path.join(ROOT, r.replace('/', os.sep))
    if os.path.exists(p):
        print(f'  [OK]   {r}')
    else:
        print(f'  [FAIL] 缺 {r}')
        bad += 1

# ── 5. 发布包不该再有说明文档 ──
rel = os.path.join(ROOT, 'release', 'Ver001')
relmd = [f for f in os.listdir(rel) if f.endswith('.md')]
print(f'\n【5】发布包里的 .md：{relmd if relmd else "（无）"}')
if relmd:
    bad += 1

print(f'\n═══ {"全部通过" if bad == 0 else str(bad) + " 处问题"} ═══')
sys.exit(1 if bad else 0)
