"""发布目录里 flash.cmd 的生成逻辑（make_release.py 调用）。

单独放一个模块，是为了让 make_release.py 专注编排流程。
★ flash.cmd 必须 GBK：cmd.exe 按系统 ANSI 代码页读批处理，存 UTF-8 会乱码。
"""
import io
import os

FLASH_CMD = r'''@echo off
chcp 936 >nul
setlocal enabledelayedexpansion
title S3_Keyboard {VER} - 烧录

set PORT=%~1
if "%PORT%"=="" set PORT=COM5

echo ============================================================
echo   S3_Keyboard  {VER}   固件烧录
echo   串口: %PORT%   （换端口:  flash.cmd COM7）
echo ============================================================
echo.

if not exist "%~dp0firmware\S3_Keyboard_{VER}.bin" (
  echo [错误] 找不到 firmware\S3_Keyboard_{VER}.bin
  echo        请在解压后的完整目录里运行本脚本。
  pause ^& exit /b 1
)

REM ---- 找一个能用的 esptool ----
set ESPTOOL=
where python ^>nul 2^>nul ^&^& set ESPTOOL=python -m esptool
if not defined ESPTOOL (
  if exist "C:\Espressif\tools\python\v6.1\venv\Scripts\python.exe" (
    set ESPTOOL="C:\Espressif\tools\python\v6.1\venv\Scripts\python.exe" -m esptool
  )
)
if not defined ESPTOOL (
  echo [错误] 没找到 esptool。
  echo        请安装 ESP-IDF，或执行:  pip install esptool
  pause ^& exit /b 1
)

echo 正在烧录（约 20 秒）...
echo.
%ESPTOOL% --chip esp32s3 --port %PORT% --baud 460800 ^
  write_flash --flash_mode dio --flash_size 16MB ^
  0x0     "%~dp0firmware\bootloader.bin" ^
  0x8000  "%~dp0firmware\partition-table.bin" ^
  0x20000 "%~dp0firmware\S3_Keyboard_{VER}.bin"

if errorlevel 1 (
  echo.
  echo [失败] 烧录出错。常见原因：
  echo        1. 串口号不对（设备管理器里看 CH343 是哪个 COM）
  echo        2. 串口被别的程序占用（关掉串口助手 / 上位机）
  echo        3. 需要按住 BOOT 再点 RST 进下载模式
  pause ^& exit /b 1
)

echo.
echo ============================================================
echo   烧录完成
echo   原生 USB 口会重新枚举为 HID 设备（VID 3554 / PID FA09）
echo   然后双击 S3_Keyboard.exe 就能用了
echo ============================================================
pause
'''



def write(rel_dir, ver, note='第一版定稿：26 槽位（含 LT/RT 扳机）、双摇杆独立死区/反向、宏 4×20 步、布局坐标可视化校准'):
    p1 = os.path.join(rel_dir, 'flash.cmd')
    io.open(p1, 'w', encoding='gbk', newline='\r\n').write(
        FLASH_CMD.replace('{VER}', ver))



    # 自检：cmd 必须能被 GBK 干净解出
    raw = io.open(p1, 'rb').read()
    txt = raw.decode('gbk')
    assert '\ufffd' not in txt, 'flash.cmd 里有 GBK 解不出来的字符'

    print(f'      flash.cmd  {os.path.getsize(p1)} 字节（GBK）')
