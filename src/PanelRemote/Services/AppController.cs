using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PanelRemote.Models;

namespace PanelRemote.Services;

/// <summary>
/// Связывает обнаружение дисплеев, Core Audio, сохранённые настройки и сценарии
/// «панель подключили», «чёрный экран», «звук не там».
/// </summary>
public sealed class AppController : IDisposable
{
    private readonly DisplayService _display;
    private readonly AudioService _audio;
    private readonly SettingsStore _settings;
    private readonly AppLog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _noticeGate = new();
    private readonly object _audioDebounceGate = new();
    private readonly object _stateGate = new();

    private CancellationTokenSource? _audioDebounce;
    private DateTime _noticeUntil;
    private string? _notice;
    private string _lastDisplayFingerprint = string.Empty;
    private string _lastAudioFingerprint = string.Empty;
    private bool _panelWasConnected;
    private bool _initialized;
    private bool _disposed;

    public AppController(
        DisplayService display,
        AudioService audio,
        SettingsStore settings,
        AppLog log)
    {
        _display = display;
        _audio = audio;
        _settings = settings;
        _log = log;
        State = new AppState(
            new DisplaySnapshot(
                DisplayMode.ComputerOnly,
                null,
                null,
                Array.Empty<DisplayTargetInfo>(),
                "проверяю…",
                0,
                false,
                null),
            new AudioSnapshot(
                Array.Empty<AudioEndpointInfo>(),
                null,
                null,
                null,
                AudioTarget.Unknown,
                false),
            false,
            false,
            null);

        _audio.DevicesChanged += Audio_DevicesChanged;
    }

    public AppState State { get; private set; }
    public DisplayService DisplayService => _display;

    public event Action<AppState>? StateChanged;

    public async Task InitializeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _audio.StartMonitoring();
        await RefreshAsync();

    }

    /// <summary>Перечитать состояние. При появлении панели возвращает сохранённые настройки.</summary>
    public async Task RefreshAsync()
    {
        if (_disposed)
        {
            return;
        }

        AppState state;
        bool justConnected;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            state = await DetectStateAsync().ConfigureAwait(false);
            justConnected = state.PanelConnected && !_panelWasConnected;
            if (justConnected)
            {
                _log.Info("Панель подключена (или появилась после запуска).");
                SetNoticeInternal("Панель подключена — восстанавливаю сохранённые настройки.", 7);
                state = state with { Notice = CurrentNotice() };
            }
            else if (!state.PanelConnected && _panelWasConnected)
            {
                _log.Info("Панель отключена. Windows показывает только компьютер.");
                SetNoticeInternal("Панель отключена. Воткните кабель HDMI, и я всё восстановлю.", 7);
                state = state with { Notice = CurrentNotice() };
            }

            _panelWasConnected = state.PanelConnected;
            _initialized = true;
            state = state with { IsInitialized = true, Notice = CurrentNotice() };
            lock (_stateGate)
            {
                State = state;
            }
        }
        catch (Exception ex)
        {
            _log.Error("Ошибка обновления состояния", ex);
            return;
        }
        finally
        {
            _gate.Release();
        }

        Publish(state);

        if (justConnected && _settings.Current.AutoRestore)
        {
            await RestoreAsync().ConfigureAwait(false);
        }
    }

    public async Task<DisplayApplyResult> ApplyDisplayModeAsync(
        DisplayMode mode,
        bool savePreference = true)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await ApplyDisplayModeCoreAsync(mode, savePreference).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AudioApplyResult> SetAudioTargetAsync(
        AudioTarget target,
        bool savePreference = true)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await SetAudioTargetCoreAsync(target, savePreference).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DiagnosticReport> DiagnoseAsync(bool fixAll)
    {
        DiagnosticReport report;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var display = await Task.Run(_display.Detect).ConfigureAwait(false);
            var requestedMode = _settings.Current.LastDisplayMode;
            var repairMode = fixAll
                ? requestedMode is DisplayMode.Duplicate or DisplayMode.Extend or DisplayMode.PanelOnly
                    ? requestedMode.Value
                    : DisplayMode.Duplicate
                : requestedMode ?? DisplayMode.Duplicate;

            if (display.PanelConnected)
            {
                _log.Info($"Диагностика: повторно применяю режим {repairMode.Short()}.");
                await Task.Run(() => _display.Apply(repairMode)).ConfigureAwait(false);
                display = await Task.Run(_display.Detect).ConfigureAwait(false);

                if (fixAll || _settings.Current.LastDisplayMode is null)
                {
                    _settings.Current.LastDisplayMode = repairMode;
                    _settings.Current.HasSavedPreferences = true;
                    _settings.Save();
                }
            }

            var audio = await Task.Run(() =>
                _audio.Detect(display.Panel?.FriendlyName, display.PanelConnected, display.Computer?.FriendlyName)).ConfigureAwait(false);

            var audioFixed = false;
            if (fixAll && display.PanelConnected)
            {
                _log.Info("Диагностика: переключаю звук на панель.");
                var audioResult = await Task.Run(() => _audio.SetDefault(
                    AudioTarget.Panel,
                    _settings.Current.PanelAudioEndpointId,
                    _settings.Current.ComputerAudioEndpointId,
                    display.Panel?.FriendlyName,
                    display.PanelConnected,
                    display.Computer?.FriendlyName)).ConfigureAwait(false);

                audio = audioResult.Snapshot;
                if (audioResult.Success)
                {
                    audioFixed = true;
                    _settings.Current.LastAudioTarget = AudioTarget.Panel;
                    _settings.Current.HasSavedPreferences = true;
                    if (!string.IsNullOrWhiteSpace(audioResult.EndpointId))
                    {
                        _settings.Current.PanelAudioEndpointId = audioResult.EndpointId;
                    }

                    _settings.Save();
                    await Task.Delay(350).ConfigureAwait(false);
                    audio = await Task.Run(() =>
                        _audio.Detect(display.Panel?.FriendlyName, display.PanelConnected, display.Computer?.FriendlyName)).ConfigureAwait(false);
                }
            }

            var soundPlayed = _audio.PlayTestSound();
            display = await Task.Run(_display.Detect).ConfigureAwait(false);
            var finalAudio = await Task.Run(() =>
                _audio.Detect(display.Panel?.FriendlyName, display.PanelConnected, display.Computer?.FriendlyName)).ConfigureAwait(false);

            var finalState = new AppState(
                display,
                finalAudio,
                display.PanelConnected,
                true,
                null);
            lock (_stateGate)
            {
                State = finalState;
            }

            _panelWasConnected = display.PanelConnected;
            Publish(finalState);
            report = BuildDiagnosticReport(display, finalAudio, fixAll, audioFixed, soundPlayed);
        }
        catch (Exception ex)
        {
            _log.Error("Ошибка диагностики", ex);
            report = new DiagnosticReport(
                "НЕ УДАЛОСЬ ПРОВЕРИТЬ",
                "Программа столкнулась с ошибкой. Подробности записаны в журнал. Попробуйте закрыть и открыть «Пульт доски».",
                new[]
                {
                    new DiagnosticItem(DiagnosticSeverity.Error, "Ошибка диагностики. Журнал: " + _log.FilePath)
                },
                true,
                "ПОВТОРИТЬ",
                _log.FilePath);
        }
        finally
        {
            _gate.Release();
        }

        return report;
    }

    private async Task<DisplayApplyResult> ApplyDisplayModeCoreAsync(
        DisplayMode mode,
        bool savePreference)
    {
        if (savePreference)
        {
            _settings.Current.LastDisplayMode = mode;
            _settings.Current.HasSavedPreferences = true;
            _settings.Save();
            _log.Info($"Пользователь выбрал режим экрана: {mode.Short()}.");
        }

        var result = await Task.Run(() => _display.Apply(mode)).ConfigureAwait(false);
        var audio = await Task.Run(() => _audio.Detect(
            result.Snapshot.Panel?.FriendlyName,
            result.Snapshot.PanelConnected,
            result.Snapshot.Computer?.FriendlyName)).ConfigureAwait(false);

        var notice = result.Success ? null : result.Message;
        if (!result.Success)
        {
            SetNoticeInternal(result.Message, 10);
        }

        var state = new AppState(
            result.Snapshot,
            audio,
            result.Snapshot.PanelConnected,
            true,
            notice ?? CurrentNotice());
        _panelWasConnected = result.Snapshot.PanelConnected;
        lock (_stateGate)
        {
            State = state;
        }

        Publish(state);
        return result;
    }

    private async Task<AudioApplyResult> SetAudioTargetCoreAsync(
        AudioTarget target,
        bool savePreference)
    {
        if (savePreference)
        {
            _settings.Current.LastAudioTarget = target;
            _settings.Current.HasSavedPreferences = true;
            _settings.Save();
            _log.Info($"Пользователь выбрал звук: {target}.");
        }

        var display = await Task.Run(_display.Detect).ConfigureAwait(false);

        // Ручная настройка администратора важнее сохранённого авто-предпочтения,
        // иначе переключение уходит на устройство, выбранное когда-то автоматикой.
        var adminConfig = _settings.Current.AdminConfig;
        var manualAudio = adminConfig?.UseManualConfiguration == true;

        var preferredPanelId = manualAudio && !string.IsNullOrWhiteSpace(adminConfig!.PanelAudioEndpointIdOverride)
            ? adminConfig.PanelAudioEndpointIdOverride
            : _settings.Current.PanelAudioEndpointId;

        var preferredComputerId = manualAudio && !string.IsNullOrWhiteSpace(adminConfig!.ComputerAudioEndpointIdOverride)
            ? adminConfig.ComputerAudioEndpointIdOverride
            : _settings.Current.ComputerAudioEndpointId;

        // Звук и изображение независимы: если панель задана вручную,
        // переключать звук можно даже когда её экран сейчас не подключён.
        var panelConnectedForAudio = display.PanelConnected
                                     || (manualAudio && !string.IsNullOrWhiteSpace(adminConfig!.PanelAudioEndpointIdOverride));

        var result = await Task.Run(() => _audio.SetDefault(
            target,
            preferredPanelId,
            preferredComputerId,
            display.Panel?.FriendlyName,
            panelConnectedForAudio,
            display.Computer?.FriendlyName)).ConfigureAwait(false);

        if (result.Success)
        {
            if (target == AudioTarget.Panel && !string.IsNullOrWhiteSpace(result.EndpointId))
            {
                _settings.Current.PanelAudioEndpointId = result.EndpointId;
            }
            else if (target == AudioTarget.Computer && !string.IsNullOrWhiteSpace(result.EndpointId))
            {
                _settings.Current.ComputerAudioEndpointId = result.EndpointId;
            }

            _settings.Current.HasSavedPreferences = true;
            _settings.Save();
        }
        else
        {
            SetNoticeInternal(result.Message, 10);
        }

        var state = new AppState(
            display,
            result.Snapshot,
            display.PanelConnected,
            true,
            result.Success ? CurrentNotice() : result.Message);
        _panelWasConnected = display.PanelConnected;
        lock (_stateGate)
        {
            State = state;
        }

        Publish(state);
        return result;
    }

    private async Task RestoreAsync()
    {
        if (!_settings.Current.HasSavedPreferences)
        {
            return;
        }

        try
        {
            _log.Info("Восстанавливаю сохранённые настройки для подключённой панели.");
            await Task.Delay(450).ConfigureAwait(false);

            if (_settings.Current.LastDisplayMode is { } mode)
            {
                await ApplyDisplayModeAsync(mode, savePreference: false).ConfigureAwait(false);

                // Повтор для режима «Дублировать» лечит зависший HDMI handshake:
                // после переподключения кабеля панель иногда остаётся чёрной,
                // хотя Windows уже считает её активной.
                if (mode == DisplayMode.Duplicate && State.PanelConnected)
                {
                    await Task.Delay(850).ConfigureAwait(false);
                    _log.Info("Повторяю переключение повтора для восстановления HDMI handshake.");
                    await ApplyDisplayModeAsync(mode, savePreference: false).ConfigureAwait(false);
                }
            }

            if (_settings.Current.LastAudioTarget is { } target)
            {
                var audioResult = await SetAudioTargetAsync(target, savePreference: false).ConfigureAwait(false);
                if (!audioResult.Success && target == AudioTarget.Panel && State.PanelConnected)
                {
                    _log.Warn("Звук панели не переключился сразу — пробую ещё раз после появления HDMI-аудио.");
                    await Task.Delay(1400).ConfigureAwait(false);
                    await SetAudioTargetAsync(target, savePreference: false).ConfigureAwait(false);
                }
            }

            SetNoticeInternal("Сохранённые настройки применены автоматически.", 6);
            var state = State with { Notice = CurrentNotice() };
            lock (_stateGate)
            {
                State = state;
            }

            Publish(state);
            _log.Info($"Восстановление завершено: экран={State.Display.CurrentMode.Short()}, " +
                      $"звук={State.Audio.CurrentTarget}.");
        }
        catch (Exception ex)
        {
            _log.Error("Ошибка восстановления настроек", ex);
        }
    }

    private async Task<AppState> DetectStateAsync()
    {
        var display = await Task.Run(_display.Detect).ConfigureAwait(false);
        var audio = await Task.Run(() =>
            _audio.Detect(display.Panel?.FriendlyName, display.PanelConnected, display.Computer?.FriendlyName)).ConfigureAwait(false);

        LogChanges(display, audio);

        return new AppState(
            display,
            audio,
            display.PanelConnected,
            _initialized,
            CurrentNotice());
    }

    private void LogChanges(DisplaySnapshot display, AudioSnapshot audio)
    {
        var displayFingerprint =
            $"{display.PanelConnected}|{display.Panel?.Key}|{display.CurrentMode}|{display.IsCloneTopology}|" +
            $"{display.PanelActive}|{display.ComputerActive}|{display.ResolutionText}|{display.Panel?.FriendlyName}";
        if (!string.Equals(displayFingerprint, _lastDisplayFingerprint, StringComparison.Ordinal))
        {
            _lastDisplayFingerprint = displayFingerprint;
            _log.Info("Состояние экрана: " +
                      $"панель={(display.Panel is null ? "нет" : display.Panel.DisplayName)}" +
                      $" ({display.Panel?.ConnectionName ?? "—"}, active={display.PanelActive}, available={display.PanelConnected}), " +
                      $"компьютер={(display.Computer is null ? "нет" : display.Computer.DisplayName)} (active={display.ComputerActive}), " +
                      $"режим={display.CurrentMode.Short()}, clone={display.IsCloneTopology}, " +
                      $"разрешение={display.ResolutionText}, healthy={display.IsHealthy}, problem={display.Problem ?? "нет"}.");
        }

        var audioFingerprint =
            $"{audio.DefaultEndpoint?.Id}|{audio.PanelEndpoint?.Id}|{audio.ComputerEndpoint?.Id}|{audio.CurrentTarget}";
        if (!string.Equals(audioFingerprint, _lastAudioFingerprint, StringComparison.Ordinal))
        {
            _lastAudioFingerprint = audioFingerprint;
            _log.Info("Состояние звука: " +
                      $"по умолчанию={audio.DefaultName} ({audio.DefaultEndpoint?.Id ?? "нет"}), " +
                      $"панель={audio.PanelEndpoint?.DisplayName ?? "не найдена"}, " +
                      $"компьютер={audio.ComputerEndpoint?.DisplayName ?? "не найден"}, " +
                      $"выбрано={audio.CurrentTarget}.");
        }
    }

    private DiagnosticReport BuildDiagnosticReport(
        DisplaySnapshot display,
        AudioSnapshot audio,
        bool fixAll,
        bool audioFixed,
        bool soundPlayed)
    {
        var items = new List<DiagnosticItem>();
        var hasProblems = false;

        // Информация о конфигурации устройств
        var adminConfig = _settings.Current.AdminConfig;
        if (adminConfig?.UseManualConfiguration == true)
        {
            items.Add(new DiagnosticItem(
                DiagnosticSeverity.Info,
                "⚙️ Используется ручная настройка устройств (режим администратора)."));
            
            if (adminConfig.PanelDisplayOverride != null)
            {
                items.Add(new DiagnosticItem(
                    DiagnosticSeverity.Info,
                    $"  Панель: {adminConfig.PanelDisplayOverride.DisplayFriendlyName} ({adminConfig.PanelDisplayOverride.ConnectionType})"));
            }
            
            if (adminConfig.ComputerDisplayOverride != null)
            {
                items.Add(new DiagnosticItem(
                    DiagnosticSeverity.Info,
                    $"  Компьютер: {adminConfig.ComputerDisplayOverride.DisplayFriendlyName} ({adminConfig.ComputerDisplayOverride.ConnectionType})"));
            }
            
            if (!string.IsNullOrWhiteSpace(adminConfig.PanelAudioEndpointIdOverride))
            {
                items.Add(new DiagnosticItem(
                    DiagnosticSeverity.Info,
                    $"  Аудио панели: настроено вручную"));
            }
            
            if (!string.IsNullOrWhiteSpace(adminConfig.ComputerAudioEndpointIdOverride))
            {
                items.Add(new DiagnosticItem(
                    DiagnosticSeverity.Info,
                    $"  Аудио компьютера: настроено вручную"));
            }
        }
        else
        {
            items.Add(new DiagnosticItem(
                DiagnosticSeverity.Info,
                "🔍 Используется автоматическое определение устройств."));
        }

        if (display.Panel is null)
        {
            items.Add(new DiagnosticItem(DiagnosticSeverity.Error, "Кабель не подключён: Windows не видит ни одного внешнего экрана."));
            items.Add(new DiagnosticItem(DiagnosticSeverity.Error, "Панель не определена. Проверьте кабель, питание панели и выбранный источник HDMI."));
            hasProblems = true;
        }
        else
        {
            items.Add(new DiagnosticItem(
                DiagnosticSeverity.Ok,
                $"Кабель подключён. Панель: {display.Panel.DisplayName}, разъём {display.Panel.ConnectionName}."));
        }

        if (display.Panel is not null && !display.PanelActive)
        {
            items.Add(new DiagnosticItem(
                DiagnosticSeverity.Error,
                "Панель определена, но изображение на неё не передаётся."));
            hasProblems = true;
        }
        else if (display.PanelActive)
        {
            items.Add(new DiagnosticItem(
                DiagnosticSeverity.Ok,
                $"Изображение идёт на панель. Режим: {display.CurrentMode.Human()}, {display.ResolutionText}."));
        }

        if (display.IsCloneTopology && !display.IsHealthy)
        {
            items.Add(new DiagnosticItem(
                DiagnosticSeverity.Warning,
                "Режим повтора включён не полностью. Я повторил переключение — проверьте панель."));
            hasProblems = true;
        }

        if (display.PanelConnected && display.CurrentMode == DisplayMode.ComputerOnly)
        {
            items.Add(new DiagnosticItem(
                DiagnosticSeverity.Warning,
                "Сейчас выбран режим «Только компьютер», поэтому на панели нет изображения."));
            hasProblems = true;
        }

        if (audio.CurrentTarget == AudioTarget.Panel)
        {
            items.Add(new DiagnosticItem(
                DiagnosticSeverity.Ok,
                $"Звук идёт на панель: {audio.DefaultName}."));
        }
        else if (audio.DefaultEndpoint is null)
        {
            items.Add(new DiagnosticItem(
                DiagnosticSeverity.Error,
                "Windows не видит ни одного устройства звука."));
            hasProblems = true;
        }
        else if (display.PanelConnected && !audio.PanelAudioAvailable)
        {
            items.Add(new DiagnosticItem(
                DiagnosticSeverity.Warning,
                "Windows не видит динамики панели. Проверьте, что панель включена и звук в ней не выключен."));
            hasProblems = true;
        }
        else
        {
            items.Add(new DiagnosticItem(
                DiagnosticSeverity.Warning,
                $"Звук идёт на колонки компьютера — нажми синюю кнопку «Звук на панель»."));
            hasProblems = true;
        }

        if (fixAll && audioFixed)
        {
            items.Add(new DiagnosticItem(
                DiagnosticSeverity.Ok,
                $"Звук переключён на панель: {audio.DefaultName}."));
        }

        if (soundPlayed)
        {
            items.Add(new DiagnosticItem(
                DiagnosticSeverity.Info,
                $"Тестовый звук отправлен на «{audio.DefaultName}». Если его не слышно — проверьте громкость на панели."));
        }
        else
        {
            items.Add(new DiagnosticItem(
                DiagnosticSeverity.Warning,
                "Тестовый звук воспроизвести не удалось. Проверьте громкость и устройство звука."));
            hasProblems = true;
        }

        string headline;
        string explanation;
        string fixLabel;

        if (display.Panel is null)
        {
            headline = "ПАНЕЛЬ НЕ ПОДКЛЮЧЕНА";
            explanation = "Воткните кабель HDMI в компьютер и в панель. На панели выберите источник HDMI. " +
                          "Если кабель уже вставлен — попробуйте другой порт или другой кабель.";
            fixLabel = "ПОВТОРИТЬ ПРОВЕРКУ";
        }
        else if (!display.PanelActive)
        {
            headline = "ПАНЕЛЬ ПОДКЛЮЧЕНА, НО ИЗОБРАЖЕНИЯ НЕТ";
            explanation = "Я уже повторил переключение и попробовал восстановить HDMI. " +
                          "Если на панели всё ещё чёрный экран — проверьте кнопку источника HDMI на самой панели " +
                          "и плотно ли вставлен кабель. Затем нажмите «Исправить всё» ещё раз.";
            fixLabel = "ИСПРАВИТЬ ВСЁ ЕЩЁ РАЗ";
        }
        else if (audio.CurrentTarget != AudioTarget.Panel)
        {
            headline = "ИЗОБРАЖЕНИЕ ЕСТЬ, ЗВУК НА КОМПЬЮТЕРЕ";
            explanation = "Звук идёт на колонки компьютера — нажми синюю кнопку «Звук на панель». " +
                          "Или нажми «Исправить звук и экран» — я переключу звук сам.";
            fixLabel = "ИСПРАВИТЬ ЗВУК И ЭКРАН";
        }
        else if (hasProblems)
        {
            headline = "ЕСТЬ ЧТО ИСПРАВИТЬ";
            explanation = "Проверьте пункты ниже. Если изображение на панели есть, а звука нет — " +
                          "увеличьте громкость на панели пультом.";
            fixLabel = "ПОВТОРИТЬ ПРОВЕРКУ";
        }
        else
        {
            headline = "ВСЁ РАБОТАЕТ";
            explanation = "Экран и звук настроены. Если на панели чёрный экран — проверьте кнопку источника HDMI " +
                          "на самой панели: Windows уже передаёт туда изображение.";
            fixLabel = "ПРОВЕРИТЬ ЕЩЁ РАЗ";
        }

        return new DiagnosticReport(
            headline,
            explanation,
            items,
            hasProblems,
            fixLabel,
            _log.FilePath);
    }

    private void SetNoticeInternal(string? text, int seconds)
    {
        lock (_noticeGate)
        {
            _notice = text;
            _noticeUntil = DateTime.UtcNow.AddSeconds(seconds);
        }
    }

    private string? CurrentNotice()
    {
        lock (_noticeGate)
        {
            if (_notice is null || DateTime.UtcNow >= _noticeUntil)
            {
                return null;
            }

            return _notice;
        }
    }

    private void Publish(AppState state)
    {
        try
        {
            StateChanged?.Invoke(state);
        }
        catch (Exception ex)
        {
            _log.Error("Ошибка обновления интерфейса", ex);
        }
    }

    private void Audio_DevicesChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        lock (_audioDebounceGate)
        {
            _audioDebounce?.Cancel();
            _audioDebounce?.Dispose();
            _audioDebounce = new CancellationTokenSource();
            var token = _audioDebounce.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(700, token).ConfigureAwait(false);
                    await RefreshAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // пришло более новое событие
                }
                catch (Exception ex)
                {
                    _log.Error("Ошибка фонового обновления после смены аудиоустройства", ex);
                }
            });
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _audio.DevicesChanged -= Audio_DevicesChanged;
        lock (_audioDebounceGate)
        {
            _audioDebounce?.Cancel();
            _audioDebounce?.Dispose();
            _audioDebounce = null;
        }

        _audio.Dispose();
        _gate.Dispose();
    }
}
