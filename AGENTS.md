# AGENTS.md — «Пульт доски»

Этот файл — краткая точка входа для следующего агента/разработчика. Если работа идёт на Windows впервые: [docs/WINDOWS-START.md](docs/WINDOWS-START.md). Полный контекст и история решений: [docs/HANDOFF.md](docs/HANDOFF.md), [docs/TECHNICAL.md](docs/TECHNICAL.md), [docs/TEST-STATUS.md](docs/TEST-STATUS.md).

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

## Режим администратора (добавлен 2026-11-08)

Реализован для случаев, когда автоопределение панели/звука не работает (несколько внешних мониторов, нестандартные HDMI-аудио). Три способа открыть:
- **Ctrl+Shift+A** (глобальная горячая клавиша)
- Трей-меню → "Настройки администратора"
- `PanelRemote.exe --admin`

Окно администратора (`AdminWindow.xaml`) позволяет вручную выбрать:
- Какой дисплей является панелью, а какой — компьютером
- Какие аудиоустройства использовать для панели и компьютера

Настройки сохраняются в `settings.json` (поле `AdminConfig`). Если выбранное устройство не найдено — fallback на автоопределение с предупреждением. Диагностика показывает источник конфигурации (авто/ручной) и выбранные устройства.

**Важно:** ручная настройка — крайняя мера. Основной сценарий — автоопределение. Не переделывай главный UI под ручную настройку.

## Команды

Windows, разработка (сборка + запуск с `--portable`):

```bat
dev-run.cmd
```

Windows, чистая сборка релиза:

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
| `src/PanelRemote/AdminWindow.xaml` | окно администратора: ручной выбор панели/компьютера и аудио |
| `src/PanelRemote/ViewModels/AdminViewModel.cs` | логика окна администратора, сохранение ручной конфигурации |
| `src/PanelRemote/Controls/ActionTile.xaml` | огромная кнопка, активное/primary/диагностическое состояние |
| `src/PanelRemote/Services/DisplayService.cs` | CCD: детект панели, 4 режима, fallback, общий режим клона |
| `src/PanelRemote/Services/AudioService.cs` | Core Audio: MMDevice, IPolicyConfig, тестовый звук |
| `src/PanelRemote/Services/AppController.cs` | сценарии, восстановление после reconnect, диагностика |
| `src/PanelRemote/Services/InstallService.cs` | установка в профиль, автозапуск, ярлыки |
| `src/PanelRemote/Services/SettingsStore.cs` | JSON-настройки, включая AdminConfig для ручной конфигурации |
| `src/PanelRemote/Interop/DisplayConfigNative.cs` | P/Invoke CCD, структуры и флаги |
| `src/PanelRemote/Interop/AudioNative.cs` | COM-контракты Core Audio/IPolicyConfig |
| `src/PanelRemote/Infrastructure/EnumToBoolConverter.cs` | конвертер для RadioButton в AdminWindow |
| `tests/InteropLayoutChecks` | регрессия размеров структур и флагов CCD |
| `docs/WINDOWS-START.md` | пошаговая инструкция для первого запуска на Windows |
| `docs/HANDOFF.md` | что было сделано, что осталось, как это проверялось |
| `docs/TEST-STATUS.md` | честная матрица проверок |
| `docs/AI-CONTEXT-PROMPT.md` | готовый промпт для нового чата |
| `.github/workflows/build.yml` | CI: Windows build + Linux cross-check |

## Ловушки, которые уже исправлены — не сломай обратно

1. **`PROPVARIANT` на x64 имеет размер 24, а не 16.** В `AudioNative.cs` стоит `[StructLayout(LayoutKind.Explicit, Size = 24)]`. Иначе native `IPropertyStore.GetValue`/`PropVariantClear` портят память.
2. **CCD-флаги:** `SDC_SAVE_TO_DATABASE = 0x200`, `SDC_ALLOW_CHANGES = 0x400`, `SDC_PATH_PERSIST_IF_REQUIRED = 0x800`, `SDC_NO_OPTIMIZATION = 0x100`.
3. **`SDC_SAVE_TO_DATABASE` допустим только с `SDC_USE_SUPPLIED_DISPLAY_CONFIG`**, а `SDC_PATH_PERSIST_IF_REQUIRED` — только с `SDC_TOPOLOGY_*`. Не смешивай.
4. Для `QueryDisplayConfig(QDC_DATABASE_CURRENT)` нужен ненулевой указатель на topology id; для `QDC_ALL_PATHS`/`QDC_ONLY_ACTIVE_PATHS` — нулевой. Один флаг режима, не OR.
5. `IPolicyConfig.SetDefaultEndpoint` — 11-й слот vtable (10 методов до него). Не меняй порядок/количество методов, даже если не вызываешь их.
6. Для clone/only-panel на стационарном ПК topology-флаги могут вести себя не так, как на ноутбуке; fallback через `SDC_USE_SUPPLIED_DISPLAY_CONFIG` уже реализован.
7. WPF в Wine не запускается из-за `MS.Internal.FontCache.Util`/URI; это не баг приложения. Для UI-проверки нужна Windows.
8. **`SDC_ALLOW_CHANGES` несовместим с `SDC_TOPOLOGY_*`.** Вместе они дают 87 (`ERROR_INVALID_PARAMETER`), и переключение молча не происходит. `SDC_ALLOW_CHANGES` допустим только с `SDC_USE_SUPPLIED_DISPLAY_CONFIG`. С `SDC_TOPOLOGY_*` работают только `SDC_APPLY` и `SDC_PATH_PERSIST_IF_REQUIRED` (проверено `SDC_VALIDATE` на Windows 11).
9. **На стационарном ПК `SDC_TOPOLOGY_INTERNAL`/`SDC_TOPOLOGY_EXTERNAL` возвращают 0, но ничего не меняют.** «Только компьютер» и «Только панель» делаются правкой набора путей через `SDC_USE_SUPPLIED_DISPLAY_CONFIG` (см. `ApplyByEditingPaths`).
10. **Баллы автоопределения панели.** HDMI 200 / USB-C 180 / DP 60, активность ±30/5, штраф за primary −60. Тип подключения обязан перевешивать активность, иначе HDMI-панель и активный DP-монитор получают равные баллы и панелью становится обычный монитор.
11. **Ручная настройка сопоставляется по EDID-пути, а не только по ключу.** `DisplayTargetInfo.Key` содержит target id, который меняется при переподключении кабеля, поэтому сохранённая вручную конфигурация «терялась». `DisplayService.ResolveManualTarget` проверяет по порядку: `MonitorDevicePath` (EDID), `DisplayKey`, затем имя + тип подключения. `AdminViewModel.MatchesOverride` повторяет ту же логику, чтобы окно показывало уже выбранные роли.
12. **`GroupName` у RadioButton должен быть один на весь список.** В `AdminWindow.xaml` было `GroupName="{Binding Display.Key}"` — у каждой строки своя группа, поэтому «Панель» можно было отметить сразу у нескольких мониторов, и сохранялся случайный (в итоге панелью записался Dell вместо HS133PC). Сейчас: `DisplayRoleGroup` и `AudioRoleGroup`.
13. **Ручной выбор не подменяется автоопределением.** Если ручная панель не видна Windows (погас HDMI-выход), `SelectPanel` возвращает `null` и пишет предупреждение. Раньше он молча брал другой монитор, и «Только панель» переключала основной экран. Разбудить панель можно штатным `SDC_TOPOLOGY_EXTEND` — это делает `AppController.InitializeAsync` и `Apply`.
14. **Ручное аудио важнее сохранённого авто-предпочтения.** `SetAudioTargetCoreAsync` передаёт в `AudioService.SetDefault` идентификаторы из `AdminConfig`, иначе переключение уходило на устройство, выбранное автоматикой ранее.
15. **Лишний `SDC_TOPOLOGY_EXTEND` вызывает моргание.** Предварительное расширение выполняется только если нужный монитор действительно выключен.

## Версия

Версия видна в интерфейсе: в главном окне рядом с бейджем «ДЛЯ ШКОЛЫ» и в шапке окна администратора. **Поднимай `<Version>` в `PanelRemote.csproj` при каждом исправлении** — иначе непонятно, какая сборка тестируется. Сейчас 1.2.0.

## Что делать дальше

1. Прогнать [docs/TEST-CHECKLIST.md](docs/TEST-CHECKLIST.md) на Windows 10/11 с реальной панелью.
2. Зафиксировать результаты в `docs/TEST-STATUS.md` (заменить «не проверено» на факты, приложить логи).
3. Проверить CCD на двух конфигурациях: ноутбук + панель и стационарный ПК + монитор + панель.
4. Проверить смену default audio на Windows 10 и 11 (IPolicyConfig — недокументированный API).
5. Проверить 1366×768 и 1920×1080, масштаб 100%/125%.
6. Подписать EXE сертификатом для школы (SmartScreen).
7. После правок запушить `main`; CI соберёт артефакт на Windows.

## Git

Приватный remote уже настроен:

```text
origin  https://github.com/bat86/panel-remote.git
branch  main
```

Если это подпапка другого приватного репозитория — скопируй содержимое этой папки в нужную подпапку и закоммить оттуда. Архив `artifacts/` в git не входит.
