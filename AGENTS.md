# AGENTS.md — «Пульт доски»

Этот файл — краткая точка входа для следующего агента/разработчика. Полный контекст и история решений: [docs/HANDOFF.md](docs/HANDOFF.md), [docs/TECHNICAL.md](docs/TECHNICAL.md), [docs/TEST-STATUS.md](docs/TEST-STATUS.md).

## Что это

Windows-приложение для школы: компьютер преподавателя подключён к интерактивной панели (HDMI/DisplayPort/USB-C). Учитель нажимает одну из семи огромных кнопок и переключает изображение/звук. Первый уровень интерфейса — только кнопки и статус, без меню и настроек. Язык интерфейса — только русский.

Рабочее название: **«Пульт доски»**. Техническое имя: `PanelRemote`.

## Статус

- Код и документация готовы.
- EXE собран: `artifacts/PanelRemote/PanelRemote.exe` (локально, не в git).
- Проверены: Windows CI-сборка WPF в GitHub Actions, кроссплатформенная сборка под `win-x64`, ABI-структуры, single-file publish, тихая установка/удаление, автозапуск и ярлыки (Wine).
- **Не проверено на реальной Windows и реальной панели.** WPF-интерфейс в Wine не запускается (ошибка font cache), CCD/Core Audio на железе не прогонялись. Не утверждай, что критерии приёмки подтверждены, пока не пройден [docs/TEST-CHECKLIST.md](docs/TEST-CHECKLIST.md) на Windows с панелью.

## Жёсткие требования

- Windows 10/11 x64, C# + WPF, .NET 8, `net8.0-windows`, `win-x64`.
- Без прав администратора: установка в `%LOCALAPPDATA%\Programs\PanelRemote`, HKCU Run, ярлыки в профиль.
- Один self-contained single-file EXE; .NET на целевой машине не нужен.
- Только русский UI; 7 главных кнопок, высота ≥150 px, шрифт ≥32 pt (сейчас `FontSize=43` DIP = 32,25 pt), высокий контраст.
- Первый уровень — только кнопки и статус. Трей-меню и диагностический оверлей — вторичные уровни.
- Не добавляй сторонние NuGet-пакеты без необходимости. Сейчас зависимостей нет.
- Не коммить `artifacts/`, `bin/`, `obj/`, логи, `.exe`, `.zip`.

## Команды

Windows:

```bat
build.cmd
```

Linux/macOS (кросс-сборка, WPF не запускается):

```bash
./scripts/build-release.sh
```

ABI-проверка отдельно:

```bash
dotnet run --project tests/InteropLayoutChecks/InteropLayoutChecks.csproj -c Release
```

Результат: `artifacts/PanelRemote/PanelRemote.exe` и `artifacts/PanelRemote-win-x64.zip`. Эти файлы в `.gitignore`.

## Карта кода

| Путь | Что делать здесь |
|---|---|
| `src/PanelRemote/MainWindow.xaml` | главный экран: 7 кнопок, статус, диагностический оверлей |
| `src/PanelRemote/Controls/ActionTile.xaml` | огромная кнопка, активное/primary/диагностическое состояние |
| `src/PanelRemote/Services/DisplayService.cs` | CCD: детект панели, 4 режима, fallback, общий режим клона |
| `src/PanelRemote/Services/AudioService.cs` | Core Audio: MMDevice, IPolicyConfig, тестовый звук |
| `src/PanelRemote/Services/AppController.cs` | сценарии, восстановление после reconnect, диагностика |
| `src/PanelRemote/Services/InstallService.cs` | установка в профиль, автозапуск, ярлыки |
| `src/PanelRemote/Interop/DisplayConfigNative.cs` | P/Invoke CCD, структуры и флаги |
| `src/PanelRemote/Interop/AudioNative.cs` | COM-контракты Core Audio/IPolicyConfig |
| `tests/InteropLayoutChecks` | регрессия размеров структур и флагов CCD |
| `docs/HANDOFF.md` | что было сделано, что осталось, как это проверялось |
| `docs/TEST-STATUS.md` | честная матрица проверок |
| `.github/workflows/build.yml` | CI: Windows build + Linux cross-check |

## Ловушки, которые уже исправлены — не сломай обратно

1. **`PROPVARIANT` на x64 имеет размер 24, а не 16.** В `AudioNative.cs` стоит `[StructLayout(LayoutKind.Explicit, Size = 24)]`. Иначе native `IPropertyStore.GetValue`/`PropVariantClear` портят память.
2. **CCD-флаги:** `SDC_SAVE_TO_DATABASE = 0x200`, `SDC_ALLOW_CHANGES = 0x400`, `SDC_PATH_PERSIST_IF_REQUIRED = 0x800`, `SDC_NO_OPTIMIZATION = 0x100`.
3. **`SDC_SAVE_TO_DATABASE` допустим только с `SDC_USE_SUPPLIED_DISPLAY_CONFIG`**, а `SDC_PATH_PERSIST_IF_REQUIRED` — только с `SDC_TOPOLOGY_*`. Не смешивай.
4. Для `QueryDisplayConfig(QDC_DATABASE_CURRENT)` нужен ненулевой указатель на topology id; для `QDC_ALL_PATHS`/`QDC_ONLY_ACTIVE_PATHS` — нулевой. Один флаг режима, не OR.
5. `IPolicyConfig.SetDefaultEndpoint` — 11-й слот vtable (10 методов до него). Не меняй порядок/количество методов, даже если не вызываешь их.
6. Для clone/only-panel на стационарном ПК topology-флаги могут вести себя не так, как на ноутбуке; fallback через `SDC_USE_SUPPLIED_DISPLAY_CONFIG` уже реализован.
7. WPF в Wine не запускается из-за `MS.Internal.FontCache.Util`/URI; это не баг приложения. Для UI-проверки нужна Windows.

## Что делать дальше

1. Прогнать [docs/TEST-CHECKLIST.md](docs/TEST-CHECKLIST.md) на Windows 10/11 с реальной панелью.
2. Зафиксировать результаты в `docs/TEST-STATUS.md` (заменить «не проверено» на факты, приложить логи).
3. Проверить CCD на двух конфигурациях: ноутбук + панель и стационарный ПК + монитор + панель.
4. Проверить смену default audio на Windows 10 и 11 (IPolicyConfig — недокументированный API).
5. Проверить 1366×768 и 1920×1080, масштаб 100%/125%.
6. Подписать EXE сертификатом для школы (SmartScreen).
7. При наличии GitHub remote — запушить `main`; CI соберёт артефакт на Windows.

## Git

Приватный remote уже настроен:

```text
origin  https://github.com/bat86/panel-remote.git
branch  main
```

Если это подпапка другого приватного репозитория — скопируй содержимое этой папки в нужную подпапку и закоммить оттуда. Архив `artifacts/` в git не входит.
