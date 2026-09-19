@echo off
chcp 936 >nul
setlocal enabledelayedexpansion
title S3_Keyboard Ver001 - 烧录

set PORT=%~1
if "%PORT%"=="" set PORT=COM5

echo ============================================================
echo   S3_Keyboard  Ver001   固件烧录
echo   串口: %PORT%   （换端口:  flash.cmd COM7）
echo ============================================================
echo.

if not exist "%~dp0firmware\S3_Keyboard_Ver001.bin" (
  echo [错误] 找不到 firmware\S3_Keyboard_Ver001.bin
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
  0x20000 "%~dp0firmware\S3_Keyboard_Ver001.bin"

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
