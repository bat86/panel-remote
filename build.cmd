@echo off
chcp 65001 >nul
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
if errorlevel 1 (
  echo.
  echo Сборка не удалась.
  exit /b 1
)
echo.
echo Готово: artifacts\PanelRemote\PanelRemote.exe
