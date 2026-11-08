using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using PanelRemote.Infrastructure;
using PanelRemote.Models;
using PanelRemote.Services;

namespace PanelRemote.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly AppController _controller;
    private readonly AppLog _log;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _noticeTimer;

    private AppState _state;
    private bool _isBusy;
    private string _busyText = string.Empty;
    private string? _noticeText;
    private bool _isDiagnosticsOpen;
    private bool _isDiagnosticRunning;
    private string _diagnosticHeadline = "ДИАГНОСТИКА";
    private string _diagnosticExplanation = "Проверяю кабель, повторно применяю режим экрана и проверяю звук.";
    private string _diagnosticFixLabel = "ИСПРАВИТЬ ВСЁ";
    private bool _diagnosticHasProblems;

    public MainViewModel(AppController controller, AppLog log)
    {
        _controller = controller;
        _log = log;
        _dispatcher = Application.Current.Dispatcher;
        _state = controller.State;

        _noticeTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(9)
        };
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            NoticeText = null;
        };

        ComputerOnlyCommand = new AsyncRelayCommand(
            () => ApplyDisplayAsync(DisplayMode.ComputerOnly),
            () => !IsBusy);
        DuplicateCommand = new AsyncRelayCommand(
            () => ApplyDisplayAsync(DisplayMode.Duplicate),
            () => !IsBusy);
        ExtendCommand = new AsyncRelayCommand(
            () => ApplyDisplayAsync(DisplayMode.Extend),
            () => !IsBusy);
        PanelOnlyCommand = new AsyncRelayCommand(
            () => ApplyDisplayAsync(DisplayMode.PanelOnly),
            () => !IsBusy);
        PanelAudioCommand = new AsyncRelayCommand(
            () => ApplyAudioAsync(AudioTarget.Panel),
            () => !IsBusy);
        ComputerAudioCommand = new AsyncRelayCommand(
            () => ApplyAudioAsync(AudioTarget.Computer),
            () => !IsBusy);
        DiagnoseCommand = new AsyncRelayCommand(
            OpenDiagnosticsAsync,
            () => !IsBusy && !IsDiagnosticsOpen);
        FixAllCommand = new AsyncRelayCommand(
            FixAllAsync,
            () => !IsDiagnosticRunning);
        CloseDiagnosticsCommand = new RelayCommand(
            CloseDiagnostics,
            () => !IsDiagnosticRunning);
        OpenLogsCommand = new RelayCommand(OpenLogs);

        _controller.StateChanged += OnControllerStateChanged;
        UpdateCommands();
    }

    public ObservableCollection<DiagnosticItem> DiagnosticItems { get; } = new();

    public ICommand ComputerOnlyCommand { get; }
    public ICommand DuplicateCommand { get; }
    public ICommand ExtendCommand { get; }
    public ICommand PanelOnlyCommand { get; }
    public ICommand PanelAudioCommand { get; }
    public ICommand ComputerAudioCommand { get; }
    public ICommand DiagnoseCommand { get; }
    public ICommand FixAllCommand { get; }
    public ICommand CloseDiagnosticsCommand { get; }
    public ICommand OpenLogsCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsNotBusy));
                UpdateCommands();
            }
        }
    }

    public bool IsNotBusy => !IsBusy;

    public string BusyText
    {
        get => _busyText;
        private set => SetProperty(ref _busyText, value);
    }

    public string? NoticeText
    {
        get => _noticeText;
        private set
        {
            if (SetProperty(ref _noticeText, value))
            {
                OnPropertyChanged(nameof(IsNoticeVisible));
            }
        }
    }

    public bool IsNoticeVisible => !string.IsNullOrWhiteSpace(NoticeText);

    public bool IsInitialized => _state.IsInitialized;

    public System.Threading.Tasks.Task RefreshAsync() => _controller.RefreshAsync();
    public bool IsPanelConnected => _state.PanelConnected;
    public bool HasPanel => _state.Display.Panel is not null;
    public string PanelName => _state.Display.Panel?.DisplayName ?? "панель";

    public string ConnectionTitle
    {
        get
        {
            if (!_state.IsInitialized)
            {
                return "ПРОВЕРЯЮ ПОДКЛЮЧЕНИЕ…";
            }

            return _state.PanelConnected
                ? "ПАНЕЛЬ ПОДКЛЮЧЕНА"
                : "ПАНЕЛЬ НЕ ПОДКЛЮЧЕНА — ВОТКНИТЕ КАБЕЛЬ HDMI";
        }
    }

    public string ConnectionDetail
    {
        get
        {
            if (!_state.IsInitialized)
            {
                return "Смотрю, что подключено к компьютеру. Это займёт секунду.";
            }

            if (_state.PanelConnected)
            {
                var panel = _state.Display.Panel;
                return panel is null
                    ? "Панель найдена."
                    : $"{panel.DisplayName} · {panel.ConnectionName}";
            }

            return "Экран и звук работают на компьютере. Воткните кабель — настройки применятся сами.";
        }
    }

    public string DisplayStatusTitle
    {
        get
        {
            if (!_state.IsInitialized)
            {
                return "Экран: проверяю…";
            }

            return _state.Display.CurrentMode switch
            {
                DisplayMode.Duplicate when _state.Display.PanelActive => "Экран: повтор ✓",
                DisplayMode.Extend when _state.Display.PanelActive => "Экран: расширен ✓",
                DisplayMode.PanelOnly when _state.Display.PanelActive => "Экран: только панель ✓",
                _ => "Экран: только компьютер"
            };
        }
    }

    public string DisplayStatusDetail
    {
        get
        {
            if (!_state.IsInitialized)
            {
                return "Определяю разрешение…";
            }

            if (!_state.PanelConnected)
            {
                return "Панель не подключена — воткните кабель HDMI";
            }

            if (!_state.Display.PanelActive)
            {
                return "Изображение на панель не выводится — нажмите «Дублировать на панель»";
            }

            return _state.Display.CurrentMode switch
            {
                DisplayMode.Duplicate => $"{_state.Display.ResolutionText} · одинаково на двух экранах",
                DisplayMode.Extend => $"{_state.Display.ResolutionText} · два разных экрана",
                DisplayMode.PanelOnly => $"{_state.Display.ResolutionText} · только на панели",
                _ => $"{_state.Display.ResolutionText} · на панель не выводится"
            };
        }
    }

    public string AudioStatusTitle
    {
        get
        {
            if (!_state.IsInitialized)
            {
                return "Звук: проверяю…";
            }

            return _state.Audio.CurrentTarget switch
            {
                AudioTarget.Panel => "Звук: панель ✓",
                AudioTarget.Computer => "Звук: компьютер ✓",
                _ => "Звук: устройство не выбрано"
            };
        }
    }

    public string AudioStatusDetail
    {
        get
        {
            if (!_state.IsInitialized)
            {
                return "Определяю, куда идёт звук…";
            }

            if (_state.Audio.DefaultEndpoint is null)
            {
                return "Windows не видит устройство воспроизведения";
            }

            if (_state.PanelConnected
                && !_state.Audio.PanelAudioAvailable
                && _state.Audio.CurrentTarget != AudioTarget.Panel)
            {
                return $"{_state.Audio.DefaultName} · динамики панели не найдены";
            }

            return _state.Audio.DefaultName;
        }
    }

    public bool IsComputerOnlyActive =>
        _state.IsInitialized
        && _state.Display.CurrentMode == DisplayMode.ComputerOnly
        && !_state.Display.PanelActive;

    public bool IsDuplicateActive =>
        _state.IsInitialized
        && _state.Display.CurrentMode == DisplayMode.Duplicate
        && _state.Display.PanelActive;

    public bool IsExtendActive =>
        _state.IsInitialized
        && _state.Display.CurrentMode == DisplayMode.Extend
        && _state.Display.PanelActive;

    public bool IsPanelOnlyActive =>
        _state.IsInitialized
        && _state.Display.CurrentMode == DisplayMode.PanelOnly
        && _state.Display.PanelActive;

    public bool IsPanelAudioActive =>
        _state.IsInitialized && _state.Audio.CurrentTarget == AudioTarget.Panel;

    public bool IsComputerAudioActive =>
        _state.IsInitialized && _state.Audio.CurrentTarget == AudioTarget.Computer;

    public string FooterHint => "F8 — дублировать    ·    F9 — звук на панель";

    public string LogPath => _log.FilePath;

    public bool IsDiagnosticsOpen
    {
        get => _isDiagnosticsOpen;
        private set
        {
            if (SetProperty(ref _isDiagnosticsOpen, value))
            {
                UpdateCommands();
            }
        }
    }

    public bool IsDiagnosticRunning
    {
        get => _isDiagnosticRunning;
        private set
        {
            if (SetProperty(ref _isDiagnosticRunning, value))
            {
                UpdateCommands();
            }
        }
    }

    public string DiagnosticHeadline
    {
        get => _diagnosticHeadline;
        private set => SetProperty(ref _diagnosticHeadline, value);
    }

    public string DiagnosticExplanation
    {
        get => _diagnosticExplanation;
        private set => SetProperty(ref _diagnosticExplanation, value);
    }

    public string DiagnosticFixLabel
    {
        get => _diagnosticFixLabel;
        private set => SetProperty(ref _diagnosticFixLabel, value);
    }

    public bool DiagnosticHasProblems
    {
        get => _diagnosticHasProblems;
        private set => SetProperty(ref _diagnosticHasProblems, value);
    }

    public string DiagnosticLogPath => "Журнал: " + _log.FilePath;

    public void ExecuteDuplicateShortcut()
    {
        if (DuplicateCommand.CanExecute(null))
        {
            DuplicateCommand.Execute(null);
        }
    }

    public void ExecutePanelAudioShortcut()
    {
        if (PanelAudioCommand.CanExecute(null))
        {
            PanelAudioCommand.Execute(null);
        }
    }

    private async System.Threading.Tasks.Task ApplyDisplayAsync(DisplayMode mode)
    {
        IsBusy = true;
        BusyText = mode == DisplayMode.Duplicate ? "ВКЛЮЧАЮ ПОВТОР НА ПАНЕЛИ…" : "ПЕРЕКЛЮЧАЮ ЭКРАН…";
        try
        {
            var result = await _controller.ApplyDisplayModeAsync(mode);
            ApplyState(_controller.State);
            if (!result.Success)
            {
                ShowNotice(result.Message);
            }
        }
        catch (Exception ex)
        {
            _log.Error("Ошибка команды переключения экрана", ex);
            ShowNotice("Не удалось переключить экран. Нажмите «Диагностика».");
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }
    }

    private async System.Threading.Tasks.Task ApplyAudioAsync(AudioTarget target)
    {
        IsBusy = true;
        BusyText = target == AudioTarget.Panel ? "ПЕРЕКЛЮЧАЮ ЗВУК НА ПАНЕЛЬ…" : "ПЕРЕКЛЮЧАЮ ЗВУК НА КОМПЬЮТЕР…";
        try
        {
            var result = await _controller.SetAudioTargetAsync(target);
            ApplyState(_controller.State);
            if (!result.Success)
            {
                ShowNotice(result.Message);
            }
            else
            {
                ShowNotice(target == AudioTarget.Panel
                    ? "Звук теперь идёт на панель."
                    : "Звук теперь идёт на компьютер.", 4);
            }
        }
        catch (Exception ex)
        {
            _log.Error("Ошибка команды переключения звука", ex);
            ShowNotice("Не удалось переключить звук. Нажмите «Диагностика».");
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }
    }

    private async System.Threading.Tasks.Task OpenDiagnosticsAsync()
    {
        IsDiagnosticsOpen = true;
        IsDiagnosticRunning = true;
        DiagnosticHeadline = "ПРОВЕРЯЮ…";
        DiagnosticExplanation = "Проверяю кабель, повторно применяю режим экрана и проверяю звук. Это может занять несколько секунд.";
        DiagnosticFixLabel = "ИСПРАВИТЬ ВСЁ";
        DiagnosticItems.Clear();

        try
        {
            var report = await _controller.DiagnoseAsync(fixAll: false);
            ApplyReport(report);
            ApplyState(_controller.State);
        }
        catch (Exception ex)
        {
            _log.Error("Ошибка открытия диагностики", ex);
            DiagnosticHeadline = "ОШИБКА";
            DiagnosticExplanation = "Не удалось выполнить диагностику. Откройте журнал и покажите его техническому специалисту.";
            DiagnosticItems.Clear();
            DiagnosticItems.Add(new DiagnosticItem(DiagnosticSeverity.Error, ex.Message));
            DiagnosticFixLabel = "ПОВТОРИТЬ";
            DiagnosticHasProblems = true;
        }
        finally
        {
            IsDiagnosticRunning = false;
            UpdateCommands();
        }
    }

    private async System.Threading.Tasks.Task FixAllAsync()
    {
        IsDiagnosticRunning = true;
        DiagnosticHeadline = "ИСПРАВЛЯЮ…";
        DiagnosticExplanation = "Заново включаю изображение на панели и переключаю звук.";
        DiagnosticItems.Clear();

        try
        {
            var report = await _controller.DiagnoseAsync(fixAll: true);
            ApplyReport(report);
            ApplyState(_controller.State);
        }
        catch (Exception ex)
        {
            _log.Error("Ошибка исправления", ex);
            DiagnosticHeadline = "ОШИБКА";
            DiagnosticExplanation = "Не удалось выполнить исправление. Попробуйте ещё раз или откройте журнал.";
            DiagnosticItems.Clear();
            DiagnosticItems.Add(new DiagnosticItem(DiagnosticSeverity.Error, ex.Message));
            DiagnosticHasProblems = true;
        }
        finally
        {
            IsDiagnosticRunning = false;
            UpdateCommands();
        }
    }

    private void ApplyReport(DiagnosticReport report)
    {
        DiagnosticHeadline = report.Headline;
        DiagnosticExplanation = report.Explanation;
        DiagnosticFixLabel = report.FixLabel;
        DiagnosticHasProblems = report.HasProblems;

        DiagnosticItems.Clear();
        foreach (var item in report.Items)
        {
            DiagnosticItems.Add(item);
        }
    }

    private void CloseDiagnostics()
    {
        if (IsDiagnosticRunning)
        {
            return;
        }

        IsDiagnosticsOpen = false;
        UpdateCommands();
    }

    private void OpenLogs()
    {
        try
        {
            var directory = Path.GetDirectoryName(_log.FilePath);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{directory}\"",
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            _log.Error("Не удалось открыть папку журналов", ex);
        }
    }

    private void OnControllerStateChanged(AppState state)
    {
        if (_dispatcher.HasShutdownStarted)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            ApplyState(state);
        }
        else
        {
            _dispatcher.BeginInvoke(new Action(() => ApplyState(state)), DispatcherPriority.Background);
        }
    }

    private void ApplyState(AppState state)
    {
        _state = state;
        OnPropertyChanged(string.Empty);

        if (!string.IsNullOrWhiteSpace(state.Notice))
        {
            ShowNotice(state.Notice);
        }

        UpdateCommands();
    }

    private void ShowNotice(string? text, int seconds = 9)
    {
        NoticeText = text;
        _noticeTimer.Stop();
        if (!string.IsNullOrWhiteSpace(text))
        {
            _noticeTimer.Interval = TimeSpan.FromSeconds(seconds);
            _noticeTimer.Start();
        }
    }

    private void UpdateCommands()
    {
        (ComputerOnlyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (DuplicateCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ExtendCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (PanelOnlyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (PanelAudioCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ComputerAudioCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (DiagnoseCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (FixAllCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CloseDiagnosticsCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (OpenLogsCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}
