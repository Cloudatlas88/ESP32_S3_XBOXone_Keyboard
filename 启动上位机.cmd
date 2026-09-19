@echo off
chcp 936 >nul
title ESP32-S3 键盘配置器 —— 启动器
setlocal

set "ROOT=%~dp0"
set "PROJ=%ROOT%host\KbConfigurator\KbConfigurator.csproj"
set "DOTNET=C:\Program Files\dotnet\dotnet.exe"
set "EXE=%ROOT%host\KbConfigurator\bin\Debug\net10.0-windows\KbConfigurator.exe"

echo.
echo   ╔════════════════════════════════════════════════════╗
echo   ║       ESP32-S3 键盘配置器    KbConfigurator        ║
echo   ║       调用 .NET 编译器  →  直接显示界面             ║
echo   ╚════════════════════════════════════════════════════╝
echo.

REM ── 1. 检查 .NET SDK ──
if not exist "%DOTNET%" (
    echo   [X] 找不到 .NET SDK
    echo       期望路径: %DOTNET%
    echo.
    echo       如需安装:  winget install Microsoft.DotNet.SDK.10
    echo.
    pause
    exit /b 1
)

if not exist "%PROJ%" (
    echo   [X] 找不到工程文件: %PROJ%
    echo.
    pause
    exit /b 1
)

REM ── 2. 编译（增量构建，源码没改时约 1 秒）──
echo   [编译] KbConfigurator.csproj
"%DOTNET%" build "%PROJ%" -c Debug --nologo -v quiet
if errorlevel 1 (
    echo.
    echo   [X] 编译失败 —— 请检查上面的错误输出
    echo.
    pause
    exit /b 1
)

if not exist "%EXE%" (
    echo   [X] 编译完成但找不到可执行文件: %EXE%
    echo.
    pause
    exit /b 1
)

REM ── 3. 启动 ──
REM    无参数 → 后台拉起界面，关掉这个黑窗口
REM    带参数 → 前台运行（便于看 --selftest 的输出）
if "%~1"=="" (
    echo   [启动] 正在显示界面...
    start "" "%EXE%"
    exit /b 0
)

echo   [启动] 前台运行: %*
echo.
"%EXE%" %*
exit /b %ERRORLEVEL%