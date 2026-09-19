"""打一个定稿发布目录：release/VerNNN/

一条命令完成：发布自包含单文件 exe → 拷固件 → 生成 flash.cmd 与使用说明 → 自检。

用法：
    python tools/make_release.py Ver001
    python tools/make_release.py Ver002 --note "加了 XX"

★ 几个踩过的坑，都固化在这里了：
  1. `None` + `CopyToOutputDirectory` 对 **publish 无效**，Assets 要用
     `CopyToPublishDirectory` 才会跟着发出去（否则成品一运行就是空白页）。
  2. hidapi.dll 会被**内嵌进单文件 exe**（IncludeNativeLibrariesForSelfExtract），
     所以发布目录里不需要它 —— 但**必须实测**发布出来的 exe 能连上设备，
     光看目录里没有 dll 就以为坏了，或者反过来以为没事，都不行。
  3. flash.cmd 必须存成 **GBK**：cmd.exe 按系统 ANSI 代码页读批处理，
     存 UTF-8 的话提示语全是乱码。
  4. 打完要**校验固件里真的带 VerNNN 标记**（app 描述符里存的是 PROJECT_VER）——
     版本号取错过一次（ESP-IDF 默认从 git describe 取，显示成了 flash-NNN）。
"""
import io
import os
import shutil
import subprocess
import sys

# 控制台默认是 GBK，遇到 ✔ 这类字符会直接抛 UnicodeEncodeError 把脚本打断。
# 统一成 UTF-8 + 替换策略，输出永远不会因为字符集挂掉。
sys.stdout.reconfigure(encoding='utf-8', errors='replace')

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DOTNET = r'C:\Program Files\dotnet\dotnet.exe'
FW_BUILD = os.path.join(ROOT, 'firmware', 'main_fw', 'build')
CSPROJ = os.path.join(ROOT, 'host', 'KbConfigurator', 'KbConfigurator.csproj')


def run(cmd, **kw):
    r = subprocess.run(cmd, capture_output=True, text=True, encoding='utf-8',
                       errors='replace', **kw)
    if r.returncode != 0:
        print(r.stdout[-3000:])
        print(r.stderr[-3000:])
        raise SystemExit(f'命令失败：{" ".join(cmd)}')
    return r.stdout


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    ver = sys.argv[1]
    note = ''
    if '--note' in sys.argv:
        note = sys.argv[sys.argv.index('--note') + 1]

    rel = os.path.join(ROOT, 'release', ver)
    print(f'═══ 打发布包 {ver} ═══')
    print(f'  输出目录：{rel}')
    if note:
        print(f'  说明：{note}')
    print()

    # ── 1. 发布自包含单文件 exe ──
    print('[1/4] 发布上位机（自包含单文件，零安装）…')
    shutil.rmtree(rel, ignore_errors=True)
    os.makedirs(rel, exist_ok=True)
    run([DOTNET, 'publish', CSPROJ,
         '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
         '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
         '-p:AssemblyName=S3_Keyboard', '-p:DebugType=none',
         '-o', rel])
    exe = os.path.join(rel, 'S3_Keyboard.exe')
    if not os.path.exists(exe):
        raise SystemExit('  !! 没生成 S3_Keyboard.exe')
    print(f'      S3_Keyboard.exe  {os.path.getsize(exe)//1024//1024} MB')

    # ── 2. 固件二进制 ──
    print('[2/4] 拷固件…')
    fwdir = os.path.join(rel, 'firmware')
    os.makedirs(fwdir, exist_ok=True)
    app_name = f'S3_Keyboard_{ver}.bin'
    for src, dst in [(os.path.join(FW_BUILD, 'bootloader', 'bootloader.bin'), 'bootloader.bin'),
                     (os.path.join(FW_BUILD, 'partition_table', 'partition-table.bin'),
                      'partition-table.bin'),
                     (os.path.join(FW_BUILD, 'main_fw.bin'), app_name)]:
        if not os.path.exists(src):
            raise SystemExit(f'  !! 缺少 {src}（先在 firmware/main_fw 里 build 一次）')
        shutil.copy2(src, os.path.join(fwdir, dst))
        print(f'      firmware/{dst}  {os.path.getsize(src)} 字节')

    # ★ 版本自检：固件里必须真的带 VerNNN
    blob = io.open(os.path.join(fwdir, app_name), 'rb').read()
    if ver.encode() not in blob:
        raise SystemExit(f'  !! 固件里没有 "{ver}" 标记 —— 打错版本了。\n'
                         f'     检查 firmware/main_fw/CMakeLists.txt 的 PROJECT_VER '
                         f'和 app_config.h 的 FW_VERSION_STR。')
    print(f'      [OK] 固件内含 "{ver}"（确认是这次定稿的构建）')

    # ── 3. 说明文档 ──
    print('[3/4] 生成 flash.cmd…')
    import _reldocs_common as docs   # 同目录下的小模块
    docs.write(rel, ver)

    # ── 4. 自检 ──
    print('[4/4] 自检…')
    for a in ('Assets/layout.json', 'Assets/layout.default.json',
              'Assets/xbox-controller.png', 'flash.cmd'):
        p = os.path.join(rel, a.replace('/', os.sep))
        if not os.path.exists(p):
            raise SystemExit(f'  !! 缺少 {a}')
    print('      目录结构齐备')

    # flash.cmd 的编码
    raw = io.open(os.path.join(rel, 'flash.cmd'), 'rb').read()
    raw.decode('gbk')
    print('      flash.cmd 是合法 GBK（cmd.exe 不会乱码）')

    files = [f for f in
             (os.path.join(dp, n) for dp, _, ns in os.walk(rel) for n in ns)]
    total = sum(os.path.getsize(f) for f in files)
    print(f'\n  完成：{rel}')
    print(f'  {len(files)} 个文件，共 {total/1024/1024:.1f} MB')
    print('\n  ★ 最后一步要人工做：双击发布出来的 exe，点「连接设备」，')
    print('    握手显示 `固件 ' + ver + '` 才算真的打对了。')
    return 0


if __name__ == '__main__':
    sys.exit(main())
