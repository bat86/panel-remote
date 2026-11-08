# Матрица проверок

Дата: 2026-11-08. Окружение проверки: Linux (CachyOS), Wine 11, .NET SDK 8.0.427, без физической Windows-машины и панели.

> Это **не** подтверждение критериев приёмки. Проверки ниже в основном структурные и сборочные. Финальная приёмка — только на Windows 10/11 с реальной панелью по [TEST-CHECKLIST.md](TEST-CHECKLIST.md).

## Сводка

| Область | Проверка | Статус | Комментарий |
|---|---|---|---|
| Сборка | `dotnet build PanelRemote.sln -c Release` на Linux | PASS | 0 ошибок, 0 предупреждений |
| Сборка | GitHub Actions: Windows build + publish | PASS | run 42454708370 |
| Сборка | GitHub Actions: Linux cross-build | PASS | run 42454708370 |
| ABI | `tests/InteropLayoutChecks` | PASS | размеры структур и флаги CCD |
| Публикация | self-contained single-file `win-x64` | PASS | EXE 66 491 297 байт |
| Установка | `--install --silent` в Wine | PASS | копия + HKCU Run |
| Ярлыки | desktop + Start Menu в Wine | PASS после правки окружения | первый прогон упал из-за symlink Desktop |
| Удаление | `--uninstall` в Wine | PASS | автозапуск, ярлыки, папка |
| UI | WPF-запуск в Wine | NOT TESTED | WPF падает в Wine до окна |
| UI | макет 1366×728 | PASS (design only) | проверены сетка, переносы, контраст |
| CCD | реальные 4 режима на Windows | NOT TESTED | нужна Windows + панель |
| CCD | clone/common mode | NOT TESTED | код есть, нужен живой прогон |
| Audio | default endpoint через IPolicyConfig | NOT TESTED | нужна Windows |
| Reconnect | выдёргивание/вставка HDMI | NOT TESTED | нужна Windows + панель |
| Autostart | вход в Windows | NOT TESTED | реестр проверен, запуск нет |
| DPI | 100/125/150%, 1366/1920 | NOT TESTED | нужна Windows |
| Installer UX | SmartScreen/антивирус | NOT TESTED | EXE не подписан |

## Подробно

### 1. Сборка

Локально (Linux, .NET 8.0.427):

```bash
dotnet build PanelRemote.sln -c Release
```

Результат:

```
InteropLayoutChecks -> .../tests/InteropLayoutChecks/bin/Release/net8.0/InteropLayoutChecks.dll
PanelRemote -> .../src/PanelRemote/bin/x64/Release/net8.0-windows/PanelRemote.dll
Сборка успешно завершена. Предупреждений: 0, Ошибок: 0.
```

Почему это что-то значит: XAML-компилятор WPF, C#-компилятор и привязки ресурсов собрались. Это не проверяет поведение Win32/COM.

Затем CI собрал тот же коммит на `windows-latest`:

- job `Windows build and publish` — PASS;
- job `Linux cross-build check` — PASS;
- run: https://github.com/bat86/panel-remote/actions/runs/42454708370 (коммит `268ae32`);
- итоговый EXE для релиза взят из Windows-артефакта CI;
- последующие изменения в этом файле — только документация, код не менялся.

### 2. ABI-структуры и флаги

```bash
dotnet run --project tests/InteropLayoutChecks/InteropLayoutChecks.csproj -c Release
```

Проверяются:

- `DISPLAYCONFIG_PATH_INFO` = 72;
- `DISPLAYCONFIG_MODE_INFO` = 64;
- `DISPLAYCONFIG_TARGET_DEVICE_NAME` = 420;
- `DISPLAYCONFIG_TARGET_PREFERRED_MODE` = 80;
- `DISPLAYCONFIG_SOURCE_DEVICE_NAME` = 84;
- `DEVMODE` = 220;
- `DISPLAY_DEVICE` = 840;
- `PROPERTYKEY` = 20;
- `PROPVARIANT` = 24;
- флаги `SDC_APPLY`, `SDC_SAVE_TO_DATABASE`, `SDC_ALLOW_CHANGES`, `SDC_PATH_PERSIST_IF_REQUIRED`, `QDC_DATABASE_CURRENT`.

Историческая ценность: тест уже поймал `PROPVARIANT=16` (порча памяти) и сдвинутые CCD-флаги. Все проверки сейчас PASS.

### 3. Publish

```bash
dotnet publish src/PanelRemote/PanelRemote.csproj \
  -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:Platform=x64 -o artifacts/PanelRemote
```

Результат:

- `artifacts/PanelRemote/PanelRemote.exe`;
- PE32+ x86-64, GUI;
- 66 491 297 байт;
- SHA-256 `7256cdef88e0badfbcc471f30a78ff9293cdf34c2103aa1623ec313ea89d5b23`;
- ZIP `artifacts/PanelRemote-win-x64.zip` (60 873 563 байта).

### 4. Тихая установка/удаление (Wine)

Проверено на изолированном Wine-префиксе с финальным Windows CI EXE:

```bash
wine PanelRemote.exe --install --silent
wine PanelRemote.exe --uninstall
```

Подтверждено:

- копирование в `C:\users\<user>\AppData\Local\Programs\PanelRemote\PanelRemote.exe`;
- запись `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\PanelRemote` со значением `"…\PanelRemote.exe" --tray`;
- создание ярлыков `Пульт доски.lnk` на рабочем столе и в меню «Пуск»;
- удаление автозапуска, ярлыков и папки.

Особенность: в первом прогоне ярлыки не создались, потому что Wine-профиль имел `Desktop` как symlink на `/home/vasily/Рабочий стол`, куда sandbox не давал писать. После подмены на локальную папку ярлыки создались. Код `InstallService` был исправлен — теперь создаёт родительские каталоги перед `.lnk`.

### 5. WPF UI

Запуск EXE в Wine:

```
System.TypeInitializationException: System.Windows.Window
 ---> ... MS.Internal.FontCache.Util
 ---> System.UriFormatException: Invalid URI: The format of the URI could not be determined.
```

Это известное ограничение Wine/WPF, а не диагностированный баг приложения. На Windows не проверялось. UI оценивался по макету [preview/main-screen.png](../preview/main-screen.png) и [preview/diagnostics.png](../preview/diagnostics.png); макет не является рендером WPF.

### 6. Что нельзя считать проверенным

- Что `SetDisplayConfig` действительно переключает режимы на целевой Windows.
- Что `IPolicyConfig.SetDefaultEndpoint` меняет default audio на Win10/11.
- Что `WM_DEVICECHANGE`/polling восстанавливают настройки после физического reconnect.
- Что WPF-окно корректно выглядит при 125%/150% DPI.
- Что автозапуск срабатывает после перезагрузки.
- Что EXE проходит SmartScreen/антивирус.

## Как закрыть неизвестные

1. Windows 10 VM/машина + панель по HDMI.
2. `PanelRemote.exe --portable`, без установки.
3. Пройти [TEST-CHECKLIST.md](TEST-CHECKLIST.md).
4. Приложить журнал `%LOCALAPPDATA%\PanelRemote\logs\panel-*.log`.
5. Записать результаты сюда, заменив `NOT TESTED` на `PASS`/`FAIL` с датой и версией Windows.
6. Для audio отдельно проверить Win10 и Win11 (IPolicyConfig).
7. Для CCD отдельно проверить ноутбук + панель и десктоп + монитор + панель.
