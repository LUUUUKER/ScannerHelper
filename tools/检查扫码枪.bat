@echo off
REM ===========================================================================
REM  Scanner probe launcher.
REM
REM  The .bat exists only so the operator can double-click it. All the work is
REM  in the .ps1 next to it. -ExecutionPolicy Bypass is used because a fresh
REM  Windows refuses to run an unsigned .ps1 by double-click, and asking the
REM  warehouse to change a machine-wide policy for one read-only probe would be
REM  the wrong trade.
REM
REM  This file is kept ASCII-only on purpose: cmd.exe renders it in the OEM code
REM  page, and Chinese here would come out as garbage on some machines.
REM ===========================================================================
chcp 65001 >nul
echo.
echo Scanner device probe / Scanner Helper
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0检查扫码枪.ps1"
echo.
pause
