# Техническое описание

## Стек и границы

- C# 12, WPF, .NET 8, `net8.0-windows`, только `win-x64`.
- Публикация: self-contained, single-file, сжатие включено. .NET на целевом компьютере не нужен.
- Никаких сторонних NuGet-пакетов: только BCL, WPF, WinForms (только `NotifyIcon` для трея) и Win32/COM через P/Invoke.
- Установка и работа — от обычного пользователя (`asInvoker`). UAC не запрашивается.

> Реализация проверена сборкой, ABI-тестами и установкой в Wine; живая приёмка на Windows/панели ещё не выполнена. Статус: [TEST-STATUS.md](TEST-STATUS.md).

## Структура

| Файл | Ответственность |
|---|---|
| `Interop/DisplayConfigNative.cs` | структуры и функции CCD: `GetDisplayConfigBufferSizes`, `QueryDisplayConfig`, `SetDisplayConfig`, `DisplayConfigGetDeviceInfo` |
| `Interop/AudioNative.cs` | COM-интерфейсы Core Audio: `IMMDeviceEnumerator`, `IMMDevice`, `IPropertyStore`, `IMMNotificationClient`, `IPolicyConfig` |
| `Interop/NativeMethods.cs` | `EnumDisplayDevices`, `EnumDisplaySettings`, `ChangeDisplaySettingsEx`, `RegisterHotKey`, `WM_DEVICECHANGE`, broadcast-сообщение |
| `Services/DisplayService.cs` | обнаружение панели/компьютера, четыре режима, проверка результата, fallback, общий режим клона |
| `Services/AudioService.cs` | перечисление render-устройств, оценка «панель/компьютер», `SetDefaultEndpoint`, уведомления, тестовый WAV |
| `Services/AppController.cs` | оркестрация, восстановление после подключения, диагностика, сохранение выбора |
| `Services/InstallService.cs` | установка в `%LOCALAPPDATA%`, HKCU Run, ярлыки, удаление |
| `Services/SingleInstance.cs` | именованный mutex + широковещательное сообщение «показать окно» |
| `Services/AppLog.cs` | журнал с ротацией 30 дней / 60 файлов |
| `Services/SettingsStore.cs` | JSON-настройки, атомарная запись |
| `MainWindow.xaml(.cs)` | окно, статус, трей, F8/F9, реакция на WM_DEVICECHANGE/WM_DISPLAYCHANGE |
| `Controls/ActionTile.xaml(.cs)` | огромная кнопка с иконкой, подсказкой, активным состоянием |
| `tests/InteropLayoutChecks` | ABI-регрессия native-структур и флагов CCD |
| `scripts/build-release.sh` | кросс-сборка релиза на Linux/macOS |
| `.github/workflows/build.yml` | CI: Windows build + Linux cross-check |

## Экран

### Обнаружение

1. `GetDisplayConfigBufferSizes(QDC_ALL_PATHS)` + `QueryDisplayConfig` дают все пути source→target и массивы режимов.
2. Для каждого target через `DisplayConfigGetDeviceInfo(DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME)` читается дружественное имя и `monitorDevicePath`; для source — GDI-имя `\\.\DISPLAYn`.
3. `QDC_DATABASE_CURRENT` даёт текущий `DISPLAYCONFIG_TOPOLOGY_ID` (internal/clone/extend/external).
4. `EnumDisplayDevices` определяет основной экран Windows.
5. Внутренний экран ноутбука — output technology `INTERNAL`, `LVDS`, `DISPLAYPORT_EMBEDDED`, `UDI_EMBEDDED`.
6. Панель — внешний target (`HDMI`, `DISPLAYPORT_EXTERNAL`, `DISPLAYPORT_USB_TUNNEL`, `UDI_EXTERNAL`, `DVI`), не внутренний. При выборе учитываются:
   - активность/доступность;
   - тип разъёма (HDMI приоритетнее);
   - ключевые слова в имени (`panel`, `board`, `promethean`, `viewboard`, `newline`, `доска`, `панель` и т. п.);
   - штраф за статус «основной экран»;
   - разрешение.
7. Компьютерный экран — встроенный экран ноутбука, иначе основной монитор, исключая выбранную панель.

Если внешних целей нет, это штатное состояние: `Panel = null`, статус «Панель не подключена».

### Переключение

Основной путь — штатные топологии CCD:

| Режим | Флаг |
|---|---|
| Только компьютер | `SDC_TOPOLOGY_INTERNAL` |
| Дублировать | `SDC_TOPOLOGY_CLONE` |
| Расширить | `SDC_TOPOLOGY_EXTEND` |
| Только панель | `SDC_TOPOLOGY_EXTERNAL` |

Вызов: `SetDisplayConfig(0, NULL, 0, NULL, topology | SDC_APPLY | SDC_ALLOW_CHANGES | SDC_PATH_PERSIST_IF_REQUIRED)`. Флаги `SDC_SAVE_TO_DATABASE` и `SDC_PATH_PERSIST_IF_REQUIRED` взаимоисключающие: первый допустим только с `SDC_USE_SUPPLIED_DISPLAY_CONFIG`, второй — с топологиями. Резервная схема использует свою допустимую комбинацию: `SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_APPLY | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE`.

Затем приложение перечитывает конфигурацию и проверяет, что:

- для повтора активны оба дисплея и топология действительно clone;
- для расширения активны оба, но это не clone;
- для «только ...» активен ровно нужный дисплей.

Если топология не дала результата, используется резервная схема: из `QDC_ALL_PATHS` выбираются пути нужных target, modeInfo переиндексируются, один дисплей ставится в позицию 0,0, вызывается `SetDisplayConfig` с `SDC_USE_SUPPLIED_DISPLAY_CONFIG`. Это важно на стационарных ПК, где «internal/external» не так однозначны, как на ноутбуке.

Для повтора, если у дисплеев разные GDI-имена, приложение перечисляет режимы через `EnumDisplaySettings` и ищет максимальный общий по ширине, высоте и частоте (±1 Гц), затем пробует `ChangeDisplaySettingsEx`. Если общего списка получить нельзя, остаётся штатный выбор Windows, который для clone сам подбирает совместимый режим.

Если повтор не подтвердился, выполняется ещё одна попытка `SetDisplayConfig` — это лечит часть зависших HDMI handshake. При переподключении кабеля повтор применяется дважды с паузой.

### Горячее подключение

- `WM_DEVICECHANGE` (0x0219) и `WM_DISPLAYCHANGE` (0x007E) из `HwndSource`.
- `WM_POWERBROADCAST` после выхода из сна.
- Уведомления Core Audio `IMMNotificationClient` с дебаунсом.
- Страховочный polling раз в 5 секунд (DisplayPort/USB-C иногда не присылают WM_DEVICECHANGE).
- Дебаунс 700 мс, все операции сериализованы `SemaphoreSlim`.

## Звук

### Обнаружение

1. `MMDeviceEnumerator.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE)`.
2. Для каждого устройства: `IMMDevice.GetId`, `OpenPropertyStore` и `PKEY_Device_FriendlyName`, `PKEY_Device_DeviceDesc`, `PKEY_DeviceInterface_FriendlyName`.
3. Оценка кандидата в панель:
   - +120 за `HDMI`;
   - +110 за DisplayPort;
   - +100 за `NVIDIA/AMD High Definition Audio`, `Intel Display Audio`;
   - +55 за USB-C/USB Audio;
   - +140 за совпадение с токеном имени панели;
   - −130 за совпадение с токеном имени монитора компьютера (если имя панели не совпало);
   - штрафы за Realtek, наушники/headset.
4. Кандидат считается панелью при score ≥ 80. Если панель не подключена, кандидат не выбирается.
5. Компьютерный выход — default, если это не панель, иначе лучший не-HDMI кандидат (Realtek/Speakers).
6. Текущее состояние определяется по ID устройства по умолчанию (роль `eConsole`).

Если панель не отдаёт HDMI-аудио, `PanelEndpoint = null`; кнопка «Звук на панель» не будет вслепую переключать на случайное устройство.

### Переключение

Используется `IPolicyConfig.SetDefaultEndpoint` (CLSID `870AF99C-171D-4F9E-AF0D-E63DF40C2BC9`, IID `F8679F50-850A-41CF-9C72-430F290290C8`) для ролей `eConsole`, `eMultimedia`, `eCommunications`. Это пользовательская операция, администратор не нужен.

После вызова приложение до ~1,1 с опрашивает default endpoint и проверяет, что сменился именно нужный ID. Выбранный ID устройства сохраняется, чтобы при переподключении вернуть звук на тот же выход.

Тестовый звук — короткий WAV (740 Гц + 988 Гц), сгенерированный в памяти и проигранный через `winmm.dll PlaySound(SND_MEMORY | SND_ASYNC)`. Он идёт на текущее устройство по умолчанию, поэтому проверяет именно маршрут.

## Состояние и восстановление

`AppState` = снимок дисплеев + снимок звука + признак панели + флаг инициализации + временное сообщение.

`AppController.RefreshAsync`:

1. читает дисплеи и звук в фоне;
2. сравнивает `PanelConnected` с прошлым значением;
3. при переходе «нет → есть» публикует состояние и вызывает `RestoreAsync`;
4. `RestoreAsync` применяет `LastDisplayMode` и `LastAudioTarget`, для повтора делает второй проход handshake;
5. настройки сохраняются только после явного выбора пользователя (или «Исправить всё»).

Настройки (`%LOCALAPPDATA%\PanelRemote\settings.json`):

```json
{
  "Version": 1,
  "HasSavedPreferences": true,
  "LastDisplayMode": "Duplicate",
  "LastAudioTarget": "Panel",
  "PanelAudioEndpointId": "{0.0.0.00000000}.{...}",
  "ComputerAudioEndpointId": "{0.0.0.00000000}.{...}",
  "AlwaysOnTop": false,
  "AutoRestore": true
}
```

## Диагностика

`DiagnoseAsync(fixAll)`:

1. Применяет сохранённый режим (или Duplicate, если выбора ещё не было) и перечитывает дисплеи.
2. Проверяет: кабель/панель, активность панели, режим, разрешение, default-аудио.
3. Всегда отправляет тестовый звук на текущий выход.
4. Показывает крупный отчёт с иконками и точной причиной.
5. `fixAll=true` дополнительно:
   - при необходимости переводит режим в Duplicate;
   - принудительно переключает звук на панель;
   - сохраняет новый выбор и повторяет тестовый звук.

Ограничение: Windows не предоставляет достоверный признак «на панели физически чёрный экран». Если путь активен, ОС считает, что изображение идёт. Поэтому диагностика всегда повторно применяет режим и объясняет, что проверить на самой панели.

## Установка без администратора

- `%LOCALAPPDATA%\Programs\PanelRemote\PanelRemote.exe` — копия.
- `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` → `"…\PanelRemote.exe" --tray`.
- Ярлыки: рабочий стол и меню «Пуск», имя «Пульт доски».
- Первый запуск из любого места (кроме `--portable`) выполняет `InstallService.Install`.
- `--install --silent` — только установка без окна.
- `--uninstall` — удаление автозапуска/ярлыков, отложенное удаление папки.
- Один экземпляр: `Local\PanelRemote.SingleInstance.v1` + broadcast-сообщение, второй экземпляр показывает первое окно и завершается.

## Журналирование

- `%LOCALAPPDATA%\PanelRemote\logs\panel-YYYYMMDD.log`, UTF-8.
- Ротация: старше 30 дней и более 60 файлов.
- Пишутся:
  - версия, ОС, путь exe;
  - все найденные target-и с именем, разъёмом, активностью, разрешением, GDI-именем;
  - выбранная панель/компьютер;
  - все аудиоустройства, default, оценки кандидатов;
  - результат каждого `SetDisplayConfig` / `ChangeDisplaySettingsEx`;
  - результат `SetDefaultEndpoint`;
  - переходы «панель подключена/отключена»;
  - ошибки с полным `Exception.ToString()`.
- Журнал не содержит изображений экрана и не передаётся никуда по сети.

## Почему WPF, а не WinUI 3

- WPF без упаковки MSIX и без Windows App SDK: один self-contained EXE.
- Простой доступ к `HwndSource`, Win32-сообщениям, глобальным горячим клавишам и `NotifyIcon`.
- Достаточная производительность для статичного крупного интерфейса.
- WinUI 3 добавил бы packaging/runtime-зависимости и усложнил бы тихую установку в профиль без администратора.
- Avalonia не даёт преимуществ для Windows-only задачи с CCD/Core Audio и добавляет слой.

## Проверка ABI

`tests/InteropLayoutChecks` сверяет размеры всех нативных структур x64 (`DISPLAYCONFIG_*`, `DEVMODE`, `PROPVARIANT`, `PROPERTYKEY`) и значения флагов CCD. Запуск:

```bash
dotnet run --project tests/InteropLayoutChecks/InteropLayoutChecks.csproj
```

Это регрессионная защита от ошибок, которые компилятор C# не видит: неверный размер `PROPVARIANT` или сдвинутые флаги `SDC_*` приводят к порче памяти или `ERROR_INVALID_PARAMETER` на реальной Windows.

## Соответствие критериям приёмки

| Критерий | Реализация |
|---|---|
| 1. Одно нажатие «Дублировать» → изображение на панели | `ActionTile` → `DuplicateCommand` → `DisplayService.Apply(Duplicate)` с проверкой и повторным handshake |
| 2. Одно нажатие «Звук на панель» → звук с панели | `AudioService.SetDefault(Panel)` через `IPolicyConfig`, проверка default ID |
| 3. Выдернули/вставили HDMI → настройки восстановились | `RefreshAsync` ловит переход «нет → есть», `RestoreAsync` применяет сохранённые режим и звук |
| 4. Чёрный экран → «Диагностика» объясняет и исправляет | повторное применение режима, повтор handshake, тестовый звук, крупный отчёт и кнопка «Исправить всё» |
| 5. Пользователю не нужно ничего знать | первый уровень — только 7 огромных кнопок и статус; установка автоматическая; ошибки сформулированы бытовым языком |

## Известные ограничения

- Невозможно программно увидеть физическое отсутствие картинки, если Windows сообщает активный путь.
- При двух одинаковых внешних дисплеях эвристика выбора панели может ошибиться.
- USB-динамики панели без узнаваемого имени не всегда связываются с дисплеем.
- При 1366×768 и масштабе 125 % интерфейсу тесно; целевое разрешение — 1920×1080.
- Глобальные F8/F9 могут быть заняты другой программой; приложение пишет предупреждение в журнал и продолжает работать без них.
