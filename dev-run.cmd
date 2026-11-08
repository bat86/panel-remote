@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

echo === Пульт доски: сборка и запуск для разработки ===
echo.

tasklist /FI "IMAGENAME eq PanelRemote.exe" 2>nul | find /I "PanelRemote.exe" >nul
if not errorlevel 1 (
  echo ВНИМАНИЕ: PanelRemote.exe уже запущен.
  echo Скорее всего, это старая версия в трее. Закройте её:
  echo   иконка в трее -^> Выход
  echo Иначе новая сборка просто покажет старое окно.
  echo.
  pause
)

call build.cmd
if errorlevel 1 (
  echo.
  echo Сборка не удалась. Исправьте ошибки выше.
  pause
  exit /b 1
)

echo.
echo Запускаю новую сборку в портативном режиме...
start "" "%~dp0artifacts\PanelRemote\PanelRemote.exe" --portable
if errorlevel 1 (
  echo Не удалось запустить EXE.
  pause
  exit /b 1
)

echo Готово. Окно приложения должно открыться.
echo Выход из приложения — иконка в трее -^> Выход.
exit /b 0
