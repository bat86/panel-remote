using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using PanelRemote.Interop;
using PanelRemote.Models;

namespace PanelRemote.Services;

/// <summary>
/// Работа с Windows CCD (Connecting and Configuring Displays).
/// Определяет панель и компьютерный экран, переключает четыре режима Win+P,
/// проверяет, что оба дисплея действительно активны, и повторяет handshake.
/// </summary>
public sealed class DisplayService
{
    private static readonly string[] PanelKeywords =
    {
        "interactive", "panel", "board", "promethean", "activboard", "smart board",
        "smartboard", "viewboard", "newline", "iqboard", "ctouch", "starboard",
        "mimio", "seewo", "maxhub", "donview", "hisense", "optoma", "epson",
        "доска", "панель", "интерактив"
    };

    private readonly AppLog _log;
    private readonly SettingsStore _settings;
    private readonly object _gate = new();

    public DisplayService(AppLog log, SettingsStore settings)
    {
        _log = log;
        _settings = settings;
    }

    public DisplaySnapshot Detect()
    {
        lock (_gate)
        {
            try
            {
                var all = Query(DisplayConfigNative.QDC_ALL_PATHS);
                var topology = GetTopologyId();

                var primaryGdi = GetPrimaryGdiNames();
                var targets = BuildTargets(all.Paths, all.Modes, primaryGdi);

                var panel = SelectPanel(targets);
                var computer = SelectComputer(targets, panel);
                var active = targets.Where(t => t.Active).ToList();

                var panelActive = panel?.Active == true;
                var computerActive = computer?.Active == true;
                var twoDisplays = panel is not null && computer is not null
                    && !string.Equals(panel.Key, computer.Key, StringComparison.OrdinalIgnoreCase);

                var clone = topology == DisplayConfigNative.DISPLAYCONFIG_TOPOLOGY_CLONE
                    || (twoDisplays && panelActive && computerActive
                        && panel!.SourceId == computer!.SourceId
                        && panel.SourceId != uint.MaxValue);

                DisplayMode mode;
                if (panelActive && computerActive && twoDisplays)
                {
                    mode = clone ? DisplayMode.Duplicate : DisplayMode.Extend;
                }
                else if (panelActive)
                {
                    mode = DisplayMode.PanelOnly;
                }
                else
                {
                    mode = DisplayMode.ComputerOnly;
                }

                var resolution = BuildResolutionText(mode, panel, computer);
                var refresh = mode switch
                {
                    DisplayMode.PanelOnly => panel?.RefreshHz ?? 0,
                    DisplayMode.ComputerOnly => computer?.RefreshHz ?? 0,
                    _ => panel?.Active == true ? panel.RefreshHz : computer?.RefreshHz ?? 0
                };

                string? problem = null;
                if (panel is null)
                {
                    problem = "Панель не подключена.";
                }
                else if (!panelActive && mode != DisplayMode.ComputerOnly)
                {
                    problem = "Панель определена, но изображение на неё не передаётся.";
                }
                else if (active.Count == 0)
                {
                    problem = "Windows не сообщает об активном экране.";
                }

                return new DisplaySnapshot(
                    mode,
                    panel,
                    computer,
                    active,
                    resolution,
                    refresh,
                    clone,
                    problem);
            }
            catch (Exception ex)
            {
                _log.Error("Не удалось прочитать конфигурацию дисплеев", ex);
                return new DisplaySnapshot(
                    DisplayMode.ComputerOnly,
                    null,
                    null,
                    Array.Empty<DisplayTargetInfo>(),
                    "нет данных",
                    0,
                    false,
                    "Windows не отдала данные о дисплеях. Попробуйте перезапустить программу.");
            }
        }
    }

    public DisplayApplyResult Apply(DisplayMode mode)
    {
        lock (_gate)
        {
            try
            {
                var before = Detect();
                _log.Info($"Переключение экрана: {mode.Short()}. " +
                          $"Панель={Describe(before.Panel)}, компьютер={Describe(before.Computer)}, " +
                          $"текущий режим={before.CurrentMode.Short()}, разрешение={before.ResolutionText}");

                if (mode is DisplayMode.Duplicate or DisplayMode.Extend or DisplayMode.PanelOnly
                    && !before.PanelConnected)
                {
                    return Failure(mode, before, "Панель не подключена — воткните кабель HDMI.");
                }

                if (mode == DisplayMode.ComputerOnly && before.Computer is null)
                {
                    return Failure(mode, before, "Не найден экран компьютера.");
                }

                var code = SetTopology(mode, persistIfRequired: true);
                if (code != 0)
                {
                    _log.Warn($"SetDisplayConfig ({mode.Short()}) вернул {code} ({FormatError(code)}). Пробую без сохранения в базу.");
                    code = SetTopology(mode, persistIfRequired: false);
                }

                if (code != 0)
                {
                    _log.Warn($"Основное переключение не удалось: {code} ({FormatError(code)}). Пробую резервную схему.");
                    TryApplyCustom(mode, before);
                }

                Pause(900);
                var after = Detect();

                if (!Matches(mode, after) && mode is DisplayMode.ComputerOnly or DisplayMode.PanelOnly or DisplayMode.Extend)
                {
                    _log.Warn($"Режим {mode.Short()} не подтвердился, пробую резервную схему ещё раз.");
                    TryApplyCustom(mode, after);
                    Pause(500);
                    after = Detect();
                }

                if (mode == DisplayMode.Duplicate)
                {
                    // Частая причина чёрного экрана — незавершённый HDMI handshake.
                    // Если повтор не подтверждён, повторяем переключение ещё раз.
                    if (!after.IsHealthy)
                    {
                        _log.Warn("Повтор не подтверждён после первого переключения — повторяю HDMI handshake.");
                        SetTopology(DisplayMode.Duplicate, persistIfRequired: true);
                        Pause(1100);
                        after = Detect();
                    }

                    TryChooseCommonCloneMode(after);
                    after = Detect();
                }

                var success = Matches(mode, after);
                var message = success
                    ? mode switch
                    {
                        DisplayMode.ComputerOnly => "Экран компьютера включён.",
                        DisplayMode.Duplicate => "Изображение дублируется на панель.",
                        DisplayMode.Extend => "Экран расширен на панель.",
                        DisplayMode.PanelOnly => "Изображение выведено только на панель.",
                        _ => "Готово."
                    }
                    : mode switch
                    {
                        DisplayMode.Duplicate =>
                            "Не удалось включить повтор. Проверьте кабель и кнопку источника HDMI на панели.",
                        DisplayMode.PanelOnly =>
                            "Панель не включилась. Проверьте кабель и источник HDMI на панели.",
                        DisplayMode.Extend =>
                            "Не удалось расширить экран. Проверьте подключение панели.",
                        _ => "Не удалось переключить экран. Нажмите «Диагностика»."
                    };

                _log.Info($"Результат {mode.Short()}: success={success}, " +
                          $"текущий={after.CurrentMode.Short()}, панель активна={after.PanelActive}, " +
                          $"компьютер активен={after.ComputerActive}, clone={after.IsCloneTopology}, " +
                          $"разрешение={after.ResolutionText}.");

                return new DisplayApplyResult(success, mode, after, message);
            }
            catch (Exception ex)
            {
                _log.Error($"Ошибка переключения экрана в режим {mode.Short()}", ex);
                var snapshot = Detect();
                return Failure(mode, snapshot, "Не удалось переключить экран. Подробности записаны в журнал.");
            }
        }
    }

    private int SetTopology(DisplayMode mode, bool persistIfRequired)
    {
        var topology = mode switch
        {
            DisplayMode.ComputerOnly => DisplayConfigNative.SDC_TOPOLOGY_INTERNAL,
            DisplayMode.Duplicate => DisplayConfigNative.SDC_TOPOLOGY_CLONE,
            DisplayMode.Extend => DisplayConfigNative.SDC_TOPOLOGY_EXTEND,
            DisplayMode.PanelOnly => DisplayConfigNative.SDC_TOPOLOGY_EXTERNAL,
            _ => 0u
        };

        var flags = topology
                    | DisplayConfigNative.SDC_APPLY
                    | DisplayConfigNative.SDC_ALLOW_CHANGES;
        if (persistIfRequired)
        {
            flags |= DisplayConfigNative.SDC_PATH_PERSIST_IF_REQUIRED;
        }

        try
        {
            return DisplayConfigNative.SetDisplayConfig(0, null, 0, null, flags);
        }
        catch (Exception ex)
        {
            _log.Error("SetDisplayConfig выбросил исключение", ex);
            return -1;
        }
    }

    /// <summary>
    /// Резервная схема: оставить только нужные цели, скопировав их modeInfo.
    /// Нужна в основном на стационарных ПК, где topology INTERNAL/EXTERNAL
    /// ведёт себя иначе, чем на ноутбуке.
    /// </summary>
    private bool TryApplyCustom(DisplayMode mode, DisplaySnapshot state)
    {
        if (mode == DisplayMode.Duplicate)
        {
            // Клон нельзя надёжно собрать из произвольных путей, для него оставляем CCD-topology.
            return false;
        }

        try
        {
            var all = Query(DisplayConfigNative.QDC_ALL_PATHS);
            var wanted = new List<DisplayTargetInfo>();
            if (mode != DisplayMode.PanelOnly && state.Computer is not null)
            {
                wanted.Add(state.Computer);
            }

            if (mode != DisplayMode.ComputerOnly && state.Panel is not null)
            {
                wanted.Add(state.Panel);
            }

            if (wanted.Count == 0)
            {
                return false;
            }

            var paths = new List<DisplayConfigNative.DISPLAYCONFIG_PATH_INFO>();
            var modes = new List<DisplayConfigNative.DISPLAYCONFIG_MODE_INFO>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var nextX = 0;

            foreach (var target in wanted)
            {
                var candidates = all.Paths
                    .Where(p => TargetKey(p) == target.Key)
                    .Where(p => p.targetInfo.targetAvailable != 0 || (p.flags & DisplayConfigNative.DISPLAYCONFIG_PATH_ACTIVE) != 0)
                    .ToList();
                if (candidates.Count == 0)
                {
                    continue;
                }

                var activeCandidateIndex = candidates.FindIndex(p =>
                    (p.flags & DisplayConfigNative.DISPLAYCONFIG_PATH_ACTIVE) != 0);
                var path = activeCandidateIndex >= 0 ? candidates[activeCandidateIndex] : candidates[0];
                var sourceIndex = path.sourceInfo.modeInfoIdx;
                var targetIndex = path.targetInfo.modeInfoIdx;

                if (!seen.Add($"P:{path.targetInfo.adapterId.LowPart}:{path.targetInfo.adapterId.HighPart}:{path.targetInfo.id}:{path.sourceInfo.id}"))
                {
                    continue;
                }

                path.flags |= DisplayConfigNative.DISPLAYCONFIG_PATH_ACTIVE;

                if (sourceIndex == DisplayConfigNative.DISPLAYCONFIG_PATH_MODE_IDX_INVALID
                    || sourceIndex >= (uint)all.Modes.Length
                    || all.Modes[sourceIndex].infoType != DisplayConfigNative.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
                {
                    var preferred = GetPreferredMode(path.targetInfo);
                    var width = preferred?.width ?? target.Width;
                    var height = preferred?.height ?? target.Height;
                    if (width == 0 || height == 0)
                    {
                        continue;
                    }

                    sourceIndex = (uint)modes.Count;
                    modes.Add(new DisplayConfigNative.DISPLAYCONFIG_MODE_INFO
                    {
                        infoType = DisplayConfigNative.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE,
                        id = path.sourceInfo.id,
                        adapterId = path.sourceInfo.adapterId,
                        sourceMode = new DisplayConfigNative.DISPLAYCONFIG_SOURCE_MODE
                        {
                            width = width,
                            height = height,
                            pixelFormat = DisplayConfigNative.DISPLAYCONFIG_PIXELFORMAT_32BPP,
                            positionX = nextX,
                            positionY = 0
                        }
                    });
                    nextX += (int)width;
                }
                else
                {
                    var original = all.Modes[sourceIndex];
                    sourceIndex = (uint)modes.Count;
                    modes.Add(original);
                }

                if (targetIndex == DisplayConfigNative.DISPLAYCONFIG_PATH_MODE_IDX_INVALID
                    || targetIndex >= (uint)all.Modes.Length
                    || all.Modes[targetIndex].infoType != DisplayConfigNative.DISPLAYCONFIG_MODE_INFO_TYPE_TARGET)
                {
                    var preferred = GetPreferredMode(path.targetInfo);
                    if (preferred is null)
                    {
                        continue;
                    }

                    targetIndex = (uint)modes.Count;
                    modes.Add(new DisplayConfigNative.DISPLAYCONFIG_MODE_INFO
                    {
                        infoType = DisplayConfigNative.DISPLAYCONFIG_MODE_INFO_TYPE_TARGET,
                        id = path.targetInfo.id,
                        adapterId = path.targetInfo.adapterId,
                        targetMode = preferred.Value.targetMode
                    });
                }
                else
                {
                    var original = all.Modes[targetIndex];
                    targetIndex = (uint)modes.Count;
                    modes.Add(original);
                }

                path.sourceInfo.modeInfoIdx = sourceIndex;
                path.targetInfo.modeInfoIdx = targetIndex;
                paths.Add(path);
            }

            if (paths.Count == 0)
            {
                return false;
            }

            // Раскладываем выбранные экраны слева направо. Особенно важно для
            // резервного «Только панель»: иначе рабочий стол может остаться
            // в координатах расширенного режима (например, x=1920).
            var desktopX = 0;
            foreach (var path in paths)
            {
                var sourceModeIndex = path.sourceInfo.modeInfoIdx;
                if (sourceModeIndex == DisplayConfigNative.DISPLAYCONFIG_PATH_MODE_IDX_INVALID
                    || sourceModeIndex >= (uint)modes.Count)
                {
                    continue;
                }

                var positionedMode = modes[(int)sourceModeIndex];
                if (positionedMode.infoType != DisplayConfigNative.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
                {
                    continue;
                }

                positionedMode.sourceMode.positionX = desktopX;
                positionedMode.sourceMode.positionY = 0;
                modes[(int)sourceModeIndex] = positionedMode;
                desktopX += (int)positionedMode.sourceMode.width;
            }

            var flags = DisplayConfigNative.SDC_USE_SUPPLIED_DISPLAY_CONFIG
                        | DisplayConfigNative.SDC_APPLY
                        | DisplayConfigNative.SDC_ALLOW_CHANGES
                        | DisplayConfigNative.SDC_SAVE_TO_DATABASE;
            var result = DisplayConfigNative.SetDisplayConfig(
                (uint)paths.Count,
                paths.ToArray(),
                (uint)modes.Count,
                modes.ToArray(),
                flags);

            if (result != 0)
            {
                _log.Warn($"Резервная схема ({mode.Short()}) вернула {result} ({FormatError(result)}).");
            }
            else
            {
                _log.Info($"Резервная схема ({mode.Short()}) применила {paths.Count} пути(ей).");
            }

            return result == 0;
        }
        catch (Exception ex)
        {
            _log.Error("Ошибка резервной схемы переключения", ex);
            return false;
        }
    }

    private void TryChooseCommonCloneMode(DisplaySnapshot snapshot)
    {
        try
        {
            if (snapshot.Panel is not { Active: true } panel
                || snapshot.Computer is not { Active: true } computer
                || string.IsNullOrWhiteSpace(panel.GdiName)
                || string.IsNullOrWhiteSpace(computer.GdiName)
                || string.Equals(panel.GdiName, computer.GdiName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var panelModes = EnumerateModes(panel.GdiName);
            var computerModes = EnumerateModes(computer.GdiName);
            if (panelModes.Count == 0 || computerModes.Count == 0)
            {
                return;
            }

            var common = panelModes
                .Where(pm => computerModes.Any(cm =>
                    cm.Width == pm.Width
                    && cm.Height == pm.Height
                    && Math.Abs(cm.Refresh - pm.Refresh) <= 1))
                .GroupBy(m => (m.Width, m.Height, m.Refresh))
                .Select(g => g.First())
                .OrderByDescending(m => (long)m.Width * m.Height)
                .ThenByDescending(m => m.Refresh >= 60)
                .ThenByDescending(m => m.Refresh)
                .FirstOrDefault();

            if (common.Width == 0 || common.Height == 0)
            {
                _log.Warn("Не удалось найти общий видеорежим для повтора.");
                return;
            }

            if (panel.Width == common.Width && panel.Height == common.Height && panel.RefreshHz == common.Refresh)
            {
                _log.Info($"Повтор уже использует общий режим {common.Width}×{common.Height}@{common.Refresh} Гц.");
                return;
            }

            var devMode = new NativeMethods.DEVMODE
            {
                dmDeviceName = string.Empty,
                dmFormName = string.Empty,
                dmSize = (ushort)Marshal.SizeOf<NativeMethods.DEVMODE>(),
                dmFields = 0x00040000 | 0x00080000 | 0x00100000 | 0x00400000,
                dmBitsPerPel = 32,
                dmPelsWidth = common.Width,
                dmPelsHeight = common.Height,
                dmDisplayFrequency = (uint)common.Refresh
            };

            var target = string.IsNullOrWhiteSpace(panel.GdiName) ? computer.GdiName : panel.GdiName;
            var changeResult = NativeMethods.ChangeDisplaySettingsEx(
                target,
                ref devMode,
                IntPtr.Zero,
                NativeMethods.CDS_UPDATEREGISTRY,
                IntPtr.Zero);

            if (changeResult == 0)
            {
                _log.Info($"Для повтора выбран общий режим {common.Width}×{common.Height}@{common.Refresh} Гц.");
                Pause(700);
            }
            else
            {
                _log.Warn($"Не удалось применить общий режим через ChangeDisplaySettingsEx: {changeResult}.");
            }
        }
        catch (Exception ex)
        {
            _log.Error("Ошибка выбора общего режима для повтора", ex);
        }
    }

    private static List<(uint Width, uint Height, int Refresh)> EnumerateModes(string gdiName)
    {
        var result = new List<(uint, uint, int)>();
        try
        {
            for (uint i = 0; i < 512; i++)
            {
                var dm = new NativeMethods.DEVMODE
                {
                    dmDeviceName = string.Empty,
                    dmFormName = string.Empty,
                    dmSize = (ushort)Marshal.SizeOf<NativeMethods.DEVMODE>()
                };

                if (!NativeMethods.EnumDisplaySettings(gdiName, i, ref dm))
                {
                    break;
                }

                if (dm.dmPelsWidth >= 800 && dm.dmPelsHeight >= 600 && dm.dmBitsPerPel >= 24)
                {
                    result.Add((dm.dmPelsWidth, dm.dmPelsHeight, (int)dm.dmDisplayFrequency));
                }
            }
        }
        catch
        {
            // просто нет списка режимов
        }

        return result;
    }

    private static bool Matches(DisplayMode mode, DisplaySnapshot state) => mode switch
    {
        DisplayMode.ComputerOnly => state.ComputerActive && !state.PanelActive,
        DisplayMode.Duplicate => state.PanelActive && state.ComputerActive
                                 && state.IsCloneTopology
                                 && !string.Equals(state.Panel?.Key, state.Computer?.Key, StringComparison.OrdinalIgnoreCase),
        DisplayMode.Extend => state.PanelActive && state.ComputerActive
                              && !state.IsCloneTopology
                              && !string.Equals(state.Panel?.Key, state.Computer?.Key, StringComparison.OrdinalIgnoreCase),
        DisplayMode.PanelOnly => state.PanelActive && !state.ComputerActive,
        _ => false
    };

    private static DisplayApplyResult Failure(DisplayMode mode, DisplaySnapshot snapshot, string message)
        => new(false, mode, snapshot, message);

    private static void Pause(int milliseconds) => Thread.Sleep(milliseconds);

    private (DisplayConfigNative.DISPLAYCONFIG_PATH_INFO[] Paths, DisplayConfigNative.DISPLAYCONFIG_MODE_INFO[] Modes)
        Query(uint flags)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var sizeResult = DisplayConfigNative.GetDisplayConfigBufferSizes(
                flags,
                out var pathCount,
                out var modeCount);

            if (sizeResult != 0)
            {
                throw new InvalidOperationException(
                    $"GetDisplayConfigBufferSizes: {sizeResult} ({FormatError(sizeResult)})");
            }

            if (pathCount == 0 || modeCount == 0)
            {
                return (Array.Empty<DisplayConfigNative.DISPLAYCONFIG_PATH_INFO>(),
                    Array.Empty<DisplayConfigNative.DISPLAYCONFIG_MODE_INFO>());
            }

            var paths = new DisplayConfigNative.DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DisplayConfigNative.DISPLAYCONFIG_MODE_INFO[modeCount];
            var queryResult = DisplayConfigNative.QueryDisplayConfig(
                flags,
                ref pathCount,
                paths,
                ref modeCount,
                modes,
                IntPtr.Zero);

            if (queryResult == 122)
            {
                continue;
            }

            if (queryResult != 0)
            {
                throw new InvalidOperationException(
                    $"QueryDisplayConfig: {queryResult} ({FormatError(queryResult)})");
            }

            if (pathCount < (uint)paths.Length)
            {
                Array.Resize(ref paths, (int)pathCount);
            }

            if (modeCount < (uint)modes.Length)
            {
                Array.Resize(ref modes, (int)modeCount);
            }

            return (paths, modes);
        }

        throw new InvalidOperationException("Не удалось получить конфигурацию дисплеев: буфер постоянно меняется.");
    }

    private uint GetTopologyId()
    {
        try
        {
            var flags = DisplayConfigNative.QDC_DATABASE_CURRENT;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var result = DisplayConfigNative.GetDisplayConfigBufferSizes(
                    flags,
                    out var pathCount,
                    out var modeCount);
                if (result != 0 || pathCount == 0 || modeCount == 0)
                {
                    return 0;
                }

                var paths = new DisplayConfigNative.DISPLAYCONFIG_PATH_INFO[pathCount];
                var modes = new DisplayConfigNative.DISPLAYCONFIG_MODE_INFO[modeCount];
                result = DisplayConfigNative.QueryDisplayConfigWithTopology(
                    flags,
                    ref pathCount,
                    paths,
                    ref modeCount,
                    modes,
                    out var topology);

                if (result == 122)
                {
                    continue;
                }

                return result == 0 ? topology : 0;
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Не удалось узнать topology id: {ex.Message}");
        }

        return 0;
    }

    private List<DisplayTargetInfo> BuildTargets(
        DisplayConfigNative.DISPLAYCONFIG_PATH_INFO[] paths,
        DisplayConfigNative.DISPLAYCONFIG_MODE_INFO[] modes,
        HashSet<string> primaryGdi)
    {
        var result = new List<DisplayTargetInfo>();
        var groups = paths
            .Where(p => p.targetInfo.id != uint.MaxValue)
            .GroupBy(TargetKey, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var list = group.ToList();
            var activeIndex = list.FindIndex(p =>
                (p.flags & DisplayConfigNative.DISPLAYCONFIG_PATH_ACTIVE) != 0);
            var activePath = activeIndex >= 0
                ? (DisplayConfigNative.DISPLAYCONFIG_PATH_INFO?)list[activeIndex]
                : null;
            var chosen = activePath
                         ?? (DisplayConfigNative.DISPLAYCONFIG_PATH_INFO?)list.FirstOrDefault(p => p.targetInfo.targetAvailable != 0)
                         ?? list[0];

            var isActive = activePath.HasValue;
            var isAvailable = isActive || list.Any(p => p.targetInfo.targetAvailable != 0);
            var friendlyName = GetTargetName(chosen.targetInfo);
            var gdiName = GetSourceName(chosen.sourceInfo);
            var isPrimary = !string.IsNullOrWhiteSpace(gdiName) && primaryGdi.Contains(gdiName);

            uint width = 0;
            uint height = 0;
            var refresh = 0;
            var hasSeparateSource = false;

            if (isActive && activePath is { } active)
            {
                width = GetSourceWidth(active, modes);
                height = GetSourceHeight(active, modes);
                refresh = GetTargetRefresh(active, modes);
                if (width == 0 || height == 0)
                {
                    var targetMode = GetTargetMode(active, modes);
                    if (targetMode is { } tm)
                    {
                        width = tm.targetVideoSignalInfo.activeSize.cx;
                        height = tm.targetVideoSignalInfo.activeSize.cy;
                    }

                    if (refresh == 0)
                    {
                        refresh = active.targetInfo.refreshRate.ToHz();
                    }
                }

                hasSeparateSource = list.Any(p => (p.flags & DisplayConfigNative.DISPLAYCONFIG_PATH_ACTIVE) != 0)
                                    && active.sourceInfo.id != uint.MaxValue;
            }
            else
            {
                var preferred = GetPreferredMode(chosen.targetInfo);
                if (preferred is { } pm)
                {
                    width = pm.width;
                    height = pm.height;
                    refresh = pm.targetMode.targetVideoSignalInfo.vSyncFreq.ToHz();
                }
            }

            result.Add(new DisplayTargetInfo(
                group.Key,
                chosen.targetInfo.adapterId.LowPart,
                chosen.targetInfo.adapterId.HighPart,
                chosen.targetInfo.id,
                chosen.sourceInfo.id,
                gdiName,
                friendlyName,
                GetTargetPath(chosen.targetInfo),
                MapConnection(chosen.targetInfo.outputTechnology),
                isActive,
                isAvailable,
                IsInternalTechnology(chosen.targetInfo.outputTechnology),
                isPrimary,
                width,
                height,
                refresh,
                hasSeparateSource));
        }

        return result;
    }

    private DisplayTargetInfo? SelectPanel(List<DisplayTargetInfo> targets)
    {
        // Проверяем административную конфигурацию
        var adminConfig = _settings.Current.AdminConfig;
        if (adminConfig?.UseManualConfiguration == true 
            && adminConfig.PanelDisplayOverride is not null)
        {
            var manualPanel = targets.FirstOrDefault(t => 
                t.Key == adminConfig.PanelDisplayOverride.DisplayKey);
            
            if (manualPanel is not null)
            {
                _log.Info($"Используется ручная настройка панели: {manualPanel.DisplayName} ({manualPanel.ConnectionName})");
                return manualPanel;
            }
            else
            {
                _log.Warn($"Ручная настройка панели не найдена: {adminConfig.PanelDisplayOverride.DisplayFriendlyName}. " +
                          "Переключаюсь на автоопределение.");
            }
        }
        
        // Автоопределение (существующая логика)
        var candidates = targets
            .Where(t => t.Available && !t.IsInternal)
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        return candidates
            .OrderByDescending(PanelScore)
            .ThenByDescending(t => t.Active)
            .ThenByDescending(t => t.Width * t.Height)
            .First();
    }

    private static int PanelScore(DisplayTargetInfo target)
    {
        var score = 0;
        score += target.Active ? 30 : 5;
        score += target.Connection switch
        {
            DisplayConnectionKind.Hdmi => 80,
            DisplayConnectionKind.UsbC => 70,
            DisplayConnectionKind.DisplayPort => 55,
            DisplayConnectionKind.Dvi => 25,
            DisplayConnectionKind.Vga => 10,
            _ => 15
        };

        if (target.IsPrimary)
        {
            // Основной монитор компьютера почти всегда не панель, но если он единственный — выберем его.
            score -= 140;
        }

        var name = target.FriendlyName.ToLowerInvariant();
        foreach (var keyword in PanelKeywords)
        {
            if (name.Contains(keyword, StringComparison.Ordinal))
            {
                score += 160;
                break;
            }
        }

        if (name.Contains("monitor", StringComparison.Ordinal)
            || name.Contains("монитор", StringComparison.Ordinal))
        {
            score -= 15;
        }

        if (string.IsNullOrWhiteSpace(target.GdiName))
        {
            score -= 5;
        }

        return score;
    }

    private DisplayTargetInfo? SelectComputer(List<DisplayTargetInfo> targets, DisplayTargetInfo? panel)
    {
        // Проверяем административную конфигурацию
        var adminConfig = _settings.Current.AdminConfig;
        if (adminConfig?.UseManualConfiguration == true 
            && adminConfig.ComputerDisplayOverride is not null)
        {
            var manualComputer = targets.FirstOrDefault(t => 
                t.Key == adminConfig.ComputerDisplayOverride.DisplayKey);
            
            if (manualComputer is not null)
            {
                _log.Info($"Используется ручная настройка компьютера: {manualComputer.DisplayName} ({manualComputer.ConnectionName})");
                return manualComputer;
            }
            else
            {
                _log.Warn($"Ручная настройка компьютера не найдена: {adminConfig.ComputerDisplayOverride.DisplayFriendlyName}. " +
                          "Переключаюсь на автоопределение.");
            }
        }
        
        // Автоопределение (существующая логика)
        var available = targets.Where(t => t.Available).ToList();
        var internalCandidates = available
            .Where(t => t.IsInternal)
            .OrderByDescending(t => t.Active)
            .ThenByDescending(t => t.IsPrimary)
            .ToList();
        if (internalCandidates.Count > 0)
        {
            return internalCandidates[0];
        }

        var nonPanel = available
            .Where(t => panel is null || !string.Equals(t.Key, panel.Key, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var primary = nonPanel.FirstOrDefault(t => t.IsPrimary && t.Active);
        if (primary is not null)
        {
            return primary;
        }

        var active = nonPanel.Where(t => t.Active).ToList();
        if (active.Count > 0)
        {
            return active.OrderByDescending(t => t.Width * t.Height).First();
        }

        return nonPanel.FirstOrDefault(t => t.IsPrimary) ?? nonPanel.FirstOrDefault();
    }

    private static string BuildResolutionText(
        DisplayMode mode,
        DisplayTargetInfo? panel,
        DisplayTargetInfo? computer)
    {
        string Format(DisplayTargetInfo? target)
        {
            if (target is null || target.Width == 0 || target.Height == 0)
            {
                return "разрешение не определено";
            }

            return target.RefreshHz > 0
                ? $"{target.Width} × {target.Height} · {target.RefreshHz} Гц"
                : $"{target.Width} × {target.Height}";
        }

        return mode switch
        {
            DisplayMode.PanelOnly => Format(panel),
            DisplayMode.ComputerOnly => Format(computer),
            DisplayMode.Extend when panel is not null && computer is not null =>
                $"{computer.Width}×{computer.Height} + {panel.Width}×{panel.Height}",
            _ => Format(panel?.Active == true ? panel : computer)
        };
    }

    private static uint GetSourceWidth(
        DisplayConfigNative.DISPLAYCONFIG_PATH_INFO path,
        DisplayConfigNative.DISPLAYCONFIG_MODE_INFO[] modes)
    {
        var idx = path.sourceInfo.modeInfoIdx;
        if (idx != DisplayConfigNative.DISPLAYCONFIG_PATH_MODE_IDX_INVALID
            && idx < (uint)modes.Length
            && modes[idx].infoType == DisplayConfigNative.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
        {
            return modes[idx].sourceMode.width;
        }

        return 0;
    }

    private static uint GetSourceHeight(
        DisplayConfigNative.DISPLAYCONFIG_PATH_INFO path,
        DisplayConfigNative.DISPLAYCONFIG_MODE_INFO[] modes)
    {
        var idx = path.sourceInfo.modeInfoIdx;
        if (idx != DisplayConfigNative.DISPLAYCONFIG_PATH_MODE_IDX_INVALID
            && idx < (uint)modes.Length
            && modes[idx].infoType == DisplayConfigNative.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
        {
            return modes[idx].sourceMode.height;
        }

        return 0;
    }

    private static int GetTargetRefresh(
        DisplayConfigNative.DISPLAYCONFIG_PATH_INFO path,
        DisplayConfigNative.DISPLAYCONFIG_MODE_INFO[] modes)
    {
        var idx = path.targetInfo.modeInfoIdx;
        if (idx != DisplayConfigNative.DISPLAYCONFIG_PATH_MODE_IDX_INVALID
            && idx < (uint)modes.Length
            && modes[idx].infoType == DisplayConfigNative.DISPLAYCONFIG_MODE_INFO_TYPE_TARGET)
        {
            return modes[idx].targetMode.targetVideoSignalInfo.vSyncFreq.ToHz();
        }

        return path.targetInfo.refreshRate.ToHz();
    }

    private static DisplayConfigNative.DISPLAYCONFIG_TARGET_MODE? GetTargetMode(
        DisplayConfigNative.DISPLAYCONFIG_PATH_INFO path,
        DisplayConfigNative.DISPLAYCONFIG_MODE_INFO[] modes)
    {
        var idx = path.targetInfo.modeInfoIdx;
        if (idx != DisplayConfigNative.DISPLAYCONFIG_PATH_MODE_IDX_INVALID
            && idx < (uint)modes.Length
            && modes[idx].infoType == DisplayConfigNative.DISPLAYCONFIG_MODE_INFO_TYPE_TARGET)
        {
            return modes[idx].targetMode;
        }

        return null;
    }

    private static DisplayConfigNative.DISPLAYCONFIG_TARGET_PREFERRED_MODE? GetPreferredMode(
        DisplayConfigNative.DISPLAYCONFIG_PATH_TARGET_INFO target)
    {
        var preferred = new DisplayConfigNative.DISPLAYCONFIG_TARGET_PREFERRED_MODE
        {
            header = new DisplayConfigNative.DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DisplayConfigNative.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_PREFERRED_MODE,
                size = (uint)Marshal.SizeOf<DisplayConfigNative.DISPLAYCONFIG_TARGET_PREFERRED_MODE>(),
                adapterId = target.adapterId,
                id = target.id
            }
        };

        var result = DisplayConfigNative.DisplayConfigGetDeviceInfo(ref preferred);
        return result == 0 ? preferred : null;
    }

    private static string GetTargetName(DisplayConfigNative.DISPLAYCONFIG_PATH_TARGET_INFO target)
    {
        var name = new DisplayConfigNative.DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            header = new DisplayConfigNative.DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DisplayConfigNative.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                size = (uint)Marshal.SizeOf<DisplayConfigNative.DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                adapterId = target.adapterId,
                id = target.id
            },
            monitorFriendlyDeviceName = string.Empty,
            monitorDevicePath = string.Empty
        };

        var result = DisplayConfigNative.DisplayConfigGetDeviceInfo(ref name);
        return result == 0 ? CleanName(name.monitorFriendlyDeviceName) : string.Empty;
    }

    private static string GetTargetPath(DisplayConfigNative.DISPLAYCONFIG_PATH_TARGET_INFO target)
    {
        var name = new DisplayConfigNative.DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            header = new DisplayConfigNative.DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DisplayConfigNative.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                size = (uint)Marshal.SizeOf<DisplayConfigNative.DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                adapterId = target.adapterId,
                id = target.id
            },
            monitorFriendlyDeviceName = string.Empty,
            monitorDevicePath = string.Empty
        };

        return DisplayConfigNative.DisplayConfigGetDeviceInfo(ref name) == 0
            ? CleanName(name.monitorDevicePath)
            : string.Empty;
    }

    private static string GetSourceName(DisplayConfigNative.DISPLAYCONFIG_PATH_SOURCE_INFO source)
    {
        var name = new DisplayConfigNative.DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            header = new DisplayConfigNative.DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DisplayConfigNative.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                size = (uint)Marshal.SizeOf<DisplayConfigNative.DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                adapterId = source.adapterId,
                id = source.id
            },
            viewGdiDeviceName = string.Empty
        };

        return DisplayConfigNative.DisplayConfigGetDeviceInfo(ref name) == 0
            ? CleanName(name.viewGdiDeviceName)
            : string.Empty;
    }

    private static HashSet<string> GetPrimaryGdiNames()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (uint i = 0; i < 32; i++)
        {
            var device = new NativeMethods.DISPLAY_DEVICE
            {
                cb = Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>(),
                DeviceName = string.Empty,
                DeviceString = string.Empty,
                DeviceID = string.Empty,
                DeviceKey = string.Empty
            };

            if (!NativeMethods.EnumDisplayDevices(null, i, ref device, 0))
            {
                break;
            }

            if ((device.StateFlags & NativeMethods.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0
                && (device.StateFlags & NativeMethods.DISPLAY_DEVICE_PRIMARY_DEVICE) != 0
                && (device.StateFlags & NativeMethods.DISPLAY_DEVICE_MIRRORING_DRIVER) == 0)
            {
                result.Add(device.DeviceName);
            }
        }

        return result;
    }

    private static string TargetKey(DisplayConfigNative.DISPLAYCONFIG_PATH_INFO path)
        => $"{path.targetInfo.adapterId.LowPart}:{path.targetInfo.adapterId.HighPart}:{path.targetInfo.id}";

    private static DisplayConnectionKind MapConnection(uint technology) => technology switch
    {
        DisplayConfigNative.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_HDMI => DisplayConnectionKind.Hdmi,
        DisplayConfigNative.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EXTERNAL => DisplayConnectionKind.DisplayPort,
        DisplayConfigNative.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_USB_TUNNEL => DisplayConnectionKind.UsbC,
        DisplayConfigNative.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EXTERNAL => DisplayConnectionKind.DisplayPort,
        DisplayConfigNative.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DVI => DisplayConnectionKind.Dvi,
        DisplayConfigNative.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_VGA => DisplayConnectionKind.Vga,
        DisplayConfigNative.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL => DisplayConnectionKind.Internal,
        DisplayConfigNative.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_LVDS => DisplayConnectionKind.Internal,
        DisplayConfigNative.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED => DisplayConnectionKind.Internal,
        DisplayConfigNative.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EMBEDDED => DisplayConnectionKind.Internal,
        _ => DisplayConnectionKind.Other
    };

    private static bool IsInternalTechnology(uint technology)
        => technology is DisplayConfigNative.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL
            or DisplayConfigNative.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_LVDS
            or DisplayConfigNative.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED
            or DisplayConfigNative.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EMBEDDED;

    private static string CleanName(string? value)
        => (value ?? string.Empty).Replace("\0", string.Empty).Trim();

    private static string Describe(DisplayTargetInfo? target)
        => target is null
            ? "нет"
            : $"{target.DisplayName} ({target.ConnectionName}, active={target.Active}, available={target.Available}, " +
              $"{target.Width}×{target.Height}@{target.RefreshHz}, gdi={target.GdiName})";

    public static string FormatError(int code) => code switch
    {
        0 => "успех",
        5 => "доступ запрещён",
        31 => "общий сбой",
        87 => "неверный параметр",
        1168 => "элемент не найден",
        122 => "недостаточный буфер",
        _ => "код Windows " + code.ToString(CultureInfo.InvariantCulture)
    };
}
