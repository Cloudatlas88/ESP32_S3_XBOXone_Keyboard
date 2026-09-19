@echo off
REM ============================================================
REM  ESP32-S3 flash archive tool -- CMD wrapper
REM
REM  Purpose: bypass the PowerShell execution policy (this machine
REM           defaults to Restricted) WITHOUT changing any system
REM           setting, so it does not affect other scripts.
REM
REM  Usage:
REM    tools\flash.cmd -Project firmware\smoke_tusb_hid -Message "note"
REM    tools\flash.cmd -Project firmware\main_fw -Message "stage1" -Port COM3 -Result "OK"
REM
REM  See the comment header of tools\flash.ps1 for all options, or run:
REM    tools\flash.cmd -?
REM ============================================================
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0flash.ps1" %*
exit /b %ERRORLEVEL%
