using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using PanelRemote.Infrastructure;
using PanelRemote.Models;
using PanelRemote.Services;

namespace PanelRemote.ViewModels;

/// <summary>
/// ViewModel для окна администратора.
/// Позволяет вручную выбрать, какой дисплей — панель, какой — компьютер,
/// и какие аудиоустройства использовать.
/// </summary>
public sealed class AdminViewModel : ObservableObject
{
    private readonly AppController _controller;
    private readonly SettingsStore _settings;
    private readonly AppLog _log;
    private string _statusMessage = string.Empty;
    private bool _useManualConfiguration;

    public AdminViewModel(AppController controller, SettingsStore settings, AppLog log)
    {
        _controller = controller;
        _settings = settings;
        _log = log;
        
        DisplayItems = new ObservableCollection<DisplayItemViewModel>();
        AudioItems = new ObservableCollection<AudioItemViewModel>();
        
        SaveCommand = new AsyncRelayCommand(ExecuteSaveAsync);
        SaveAndRestartCommand = new AsyncRelayCommand(ExecuteSaveAndRestartAsync);
        ResetToAutoCommand = new RelayCommand(ExecuteResetToAuto);
        RefreshCommand = new RelayCommand(ExecuteRefresh);
        
        // Загружаем текущие настройки
        _useManualConfiguration = _settings.Current.AdminConfig?.UseManualConfiguration ?? false;
        
        // Загружаем список устройств
        RefreshDevices();
    }

    public ObservableCollection<DisplayItemViewModel> DisplayItems { get; }
    public ObservableCollection<AudioItemViewModel> AudioItems { get; }

    /// <summary>Версия сборки — чтобы было видно, какая сборка тестируется.</summary>
    public string VersionText => $"версия {AppPaths.Version}";

    /// <summary>
    /// Понятная сводка выбора: у каждого списка ровно одна панель и один компьютер,
    /// поэтому вторая отметка «Компьютер» снимает предыдущую.
    /// </summary>
    public string SelectionSummary
    {
        get
        {
            var panel = DisplayItems.FirstOrDefault(d => d.Role == DisplayRole.Panel)?.Display.DisplayName;
            var computer = DisplayItems.FirstOrDefault(d => d.Role == DisplayRole.Computer)?.Display.DisplayName;
            var panelAudio = AudioItems.FirstOrDefault(a => a.Role == AudioRole.Panel)?.Endpoint.DisplayName;
            var computerAudio = AudioItems.FirstOrDefault(a => a.Role == AudioRole.Computer)?.Endpoint.DisplayName;

            return $"Экран панели: {panel ?? "не выбран"}   ·   Экран компьютера: {computer ?? "не выбран"}\n" +
                   $"Звук панели: {panelAudio ?? "не выбран"}   ·   Звук компьютера: {computerAudio ?? "не выбран"}";
        }
    }

    public string WindowTitle => $"Настройки администратора — Пульт доски {AppPaths.Version}";
    
    public bool UseManualConfiguration
    {
        get => _useManualConfiguration;
        set => SetProperty(ref _useManualConfiguration, value);
    }
    
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }
    
    public ICommand SaveCommand { get; }
    public ICommand SaveAndRestartCommand { get; }
    public ICommand ResetToAutoCommand { get; }
    public ICommand RefreshCommand { get; }

    private void RefreshDevices()
    {
        try
        {
            // Получаем текущее состояние дисплеев и аудио
            var state = _controller.State;
            var adminConfig = _settings.Current.AdminConfig;
            
            // Заполняем список дисплеев
            DisplayItems.Clear();
            // Получаем ВСЕ доступные дисплеи (включая неактивные)
            var allDisplays = _controller.DisplayService.GetAllAvailableTargets();
            
            var manualActive = adminConfig?.UseManualConfiguration == true;

            foreach (var display in allDisplays)
            {
                var role = DisplayRole.None;

                if (manualActive && MatchesOverride(adminConfig?.PanelDisplayOverride, display))
                {
                    role = DisplayRole.Panel;
                }
                else if (manualActive && MatchesOverride(adminConfig?.ComputerDisplayOverride, display))
                {
                    role = DisplayRole.Computer;
                }

                // В авторежиме показываем, что программа определила сама:
                // иначе непонятно, что именно нужно переопределять.
                if (!manualActive)
                {
                    if (!string.IsNullOrWhiteSpace(state.Display.Panel?.Key)
                        && string.Equals(display.Key, state.Display.Panel.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        role = DisplayRole.Panel;
                    }
                    else if (!string.IsNullOrWhiteSpace(state.Display.Computer?.Key)
                             && string.Equals(display.Key, state.Display.Computer.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        role = DisplayRole.Computer;
                    }
                }

                DisplayItems.Add(new DisplayItemViewModel(display, role, OnRoleChanged));
            }
            
            // Заполняем список аудиоустройств
            AudioItems.Clear();
            foreach (var endpoint in state.Audio.Endpoints)
            {
                var role = AudioRole.None;

                if (manualActive && adminConfig?.PanelAudioEndpointIdOverride == endpoint.Id)
                {
                    role = AudioRole.Panel;
                }
                else if (manualActive && adminConfig?.ComputerAudioEndpointIdOverride == endpoint.Id)
                {
                    role = AudioRole.Computer;
                }

                if (!manualActive)
                {
                    if (!string.IsNullOrWhiteSpace(state.Audio.PanelEndpoint?.Id)
                        && state.Audio.PanelEndpoint.Id == endpoint.Id)
                    {
                        role = AudioRole.Panel;
                    }
                    else if (!string.IsNullOrWhiteSpace(state.Audio.ComputerEndpoint?.Id)
                             && state.Audio.ComputerEndpoint.Id == endpoint.Id)
                    {
                        role = AudioRole.Computer;
                    }
                }

                AudioItems.Add(new AudioItemViewModel(endpoint, role, OnRoleChanged));
            }
            
            OnRoleChanged();
        StatusMessage = $"Найдено дисплеев: {DisplayItems.Count}, аудиоустройств: {AudioItems.Count}";
        }
        catch (Exception ex)
        {
            _log.Error("Ошибка при обновлении списка устройств", ex);
            StatusMessage = "Ошибка при загрузке устройств: " + ex.Message;
        }
    }
    
    private async Task ExecuteSaveAsync()
    {
        if (!await SaveConfigurationAsync().ConfigureAwait(true))
        {
            return;
        }

        StatusMessage = UseManualConfiguration
            ? "✓ Ручная настройка сохранена и применена. Список обновлён."
            : "✓ Автоопределение включено и применено.";
    }

    private async Task ExecuteSaveAndRestartAsync()
    {
        if (!await SaveConfigurationAsync().ConfigureAwait(true))
        {
            return;
        }

        RestartApplication();
    }

    private async Task<bool> SaveConfigurationAsync()
    {
        try
        {
            // Создаём новую административную конфигурацию
            var adminConfig = new AdminConfiguration
            {
                UseManualConfiguration = UseManualConfiguration
            };
            
            if (UseManualConfiguration)
            {
                var hasPanel = DisplayItems.Any(d => d.Role == DisplayRole.Panel);
                var hasComputer = DisplayItems.Any(d => d.Role == DisplayRole.Computer);

                if (!hasPanel || !hasComputer)
                {
                    StatusMessage = !hasPanel
                        ? "Выберите дисплей панели — без этого ручной режим не сохранится."
                        : "Выберите дисплей компьютера — без этого ручной режим не сохранится.";
                    return false;
                }

                // Ищем выбранную панель
                var panelDisplay = DisplayItems.FirstOrDefault(d => d.Role == DisplayRole.Panel);
                if (panelDisplay != null)
                {
                    adminConfig.PanelDisplayOverride = new ManualDisplayMapping
                    {
                        DisplayKey = panelDisplay.Display.Key,
                        MonitorDevicePath = panelDisplay.Display.DevicePath,
                        DisplayFriendlyName = panelDisplay.Display.FriendlyName,
                        ConnectionType = panelDisplay.Display.ConnectionName
                    };
                }
                
                // Ищем выбранный компьютер
                var computerDisplay = DisplayItems.FirstOrDefault(d => d.Role == DisplayRole.Computer);
                if (computerDisplay != null)
                {
                    adminConfig.ComputerDisplayOverride = new ManualDisplayMapping
                    {
                        DisplayKey = computerDisplay.Display.Key,
                        MonitorDevicePath = computerDisplay.Display.DevicePath,
                        DisplayFriendlyName = computerDisplay.Display.FriendlyName,
                        ConnectionType = computerDisplay.Display.ConnectionName
                    };
                }
                
                // Ищем выбранное аудио панели
                var panelAudio = AudioItems.FirstOrDefault(a => a.Role == AudioRole.Panel);
                if (panelAudio != null)
                {
                    adminConfig.PanelAudioEndpointIdOverride = panelAudio.Endpoint.Id;
                }
                
                // Ищем выбранное аудио компьютера
                var computerAudio = AudioItems.FirstOrDefault(a => a.Role == AudioRole.Computer);
                if (computerAudio != null)
                {
                    adminConfig.ComputerAudioEndpointIdOverride = computerAudio.Endpoint.Id;
                }
            }
            
            // Сохраняем конфигурацию
            _settings.Current.AdminConfig = adminConfig;
            _settings.Save();
            
            _log.Info($"Административная конфигурация сохранена: режим={(UseManualConfiguration ? "ручной" : "автоопределение")}");

            // Применяем сразу, не дожидаясь перезапуска.
            StatusMessage = "Сохраняю и применяю…";
            await _controller.RefreshAsync().ConfigureAwait(true);
            RefreshDevices();
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("Ошибка при сохранении административной конфигурации", ex);
            StatusMessage = "Ошибка при сохранении: " + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Перезапускает программу, чтобы настройки гарантированно применились
    /// с чистого листа (и не приходилось искать ярлык в трее).
    /// </summary>
    private void RestartApplication()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe))
            {
                StatusMessage = "Не удалось определить путь к программе. Перезапустите вручную.";
                return;
            }

            // Небольшая пауза нужна, чтобы текущий процесс успел выйти
            // и не сработала защита от второго экземпляра.
            var arguments = $"/c ping -n 3 127.0.0.1 >nul & start \"\" \"{exe}\" --tray";
            Process.Start(new ProcessStartInfo("cmd.exe", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });

            StatusMessage = "Перезапускаю…";
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            _log.Error("Не удалось перезапустить приложение", ex);
            StatusMessage = "Не удалось перезапустить: " + ex.Message + ". Запустите программу вручную.";
        }
    }
    
    /// <summary>
    /// Сравнивает дисплей с сохранённым переопределением так же, как это делает
    /// DisplayService: путь EDID, затем ключ, затем имя с типом подключения.
    /// Иначе окно не показывает уже выбранные устройства.
    /// </summary>
    private static bool MatchesOverride(ManualDisplayMapping? mapping, DisplayTargetInfo display)
    {
        if (mapping is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(mapping.MonitorDevicePath)
            && !string.IsNullOrWhiteSpace(display.DevicePath)
            && string.Equals(display.DevicePath, mapping.MonitorDevicePath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(mapping.DisplayKey)
            && string.Equals(display.Key, mapping.DisplayKey, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(mapping.DisplayFriendlyName)
               && string.Equals(display.DisplayName, mapping.DisplayFriendlyName, StringComparison.OrdinalIgnoreCase)
               && (string.IsNullOrWhiteSpace(mapping.ConnectionType)
                   || string.Equals(display.ConnectionName, mapping.ConnectionType, StringComparison.OrdinalIgnoreCase));
    }

    private void OnRoleChanged()
    {
        OnPropertyChanged(nameof(SelectionSummary));
    }

    private void ExecuteResetToAuto()
    {
        UseManualConfiguration = false;
        
        // Сбрасываем выбор на всех устройствах
        foreach (var display in DisplayItems)
        {
            display.Role = DisplayRole.None;
        }
        
        foreach (var audio in AudioItems)
        {
            audio.Role = AudioRole.None;
        }
        
        StatusMessage = "Сброшено к автоопределению. Нажмите \"Сохранить\" для применения.";
    }
    
    private void ExecuteRefresh()
    {
        RefreshDevices();
    }
}

public enum DisplayRole
{
    None,
    Panel,
    Computer
}

public enum AudioRole
{
    None,
    Panel,
    Computer
}

/// <summary>
/// ViewModel для отдельного дисплея в списке администратора.
/// </summary>
public sealed class DisplayItemViewModel : ObservableObject
{
    private DisplayRole _role;
    private readonly Action? _changed;

    public DisplayItemViewModel(DisplayTargetInfo display, DisplayRole role, Action? changed = null)
    {
        Display = display;
        _role = role;
        _changed = changed;
    }
    
    public DisplayTargetInfo Display { get; }
    
    public DisplayRole Role
    {
        get => _role;
        set
        {
            if (SetProperty(ref _role, value))
            {
                _changed?.Invoke();
            }
        }
    }
    
    public string DisplayInfo => 
        $"{Display.DisplayName} | {Display.ConnectionName} | {Display.ResolutionText} | " +
        $"{(Display.Active ? "✓ Активен" : "○ Доступен")} | " +
        $"{(Display.IsPrimary ? "Основной" : "")}";
}

/// <summary>
/// ViewModel для отдельного аудиоустройства в списке администратора.
/// </summary>
public sealed class AudioItemViewModel : ObservableObject
{
    private AudioRole _role;
    private readonly Action? _changed;

    public AudioItemViewModel(AudioEndpointInfo endpoint, AudioRole role, Action? changed = null)
    {
        Endpoint = endpoint;
        _role = role;
        _changed = changed;
    }
    
    public AudioEndpointInfo Endpoint { get; }
    
    public AudioRole Role
    {
        get => _role;
        set
        {
            if (SetProperty(ref _role, value))
            {
                _changed?.Invoke();
            }
        }
    }
    
    public string AudioInfo => 
        $"{Endpoint.DisplayName} | {Endpoint.DeviceDescription} | " +
        $"{(Endpoint.IsDefault ? "✓ По умолчанию" : "")} | " +
        $"{(Endpoint.IsPanelCandidate ? $"Оценка панели: {Endpoint.PanelScore}" : "")}";
}
