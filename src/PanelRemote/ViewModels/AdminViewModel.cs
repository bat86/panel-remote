using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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
        
        SaveCommand = new RelayCommand(ExecuteSave);
        ResetToAutoCommand = new RelayCommand(ExecuteResetToAuto);
        RefreshCommand = new RelayCommand(ExecuteRefresh);
        
        // Загружаем текущие настройки
        _useManualConfiguration = _settings.Current.AdminConfig?.UseManualConfiguration ?? false;
        
        // Загружаем список устройств
        RefreshDevices();
    }

    public ObservableCollection<DisplayItemViewModel> DisplayItems { get; }
    public ObservableCollection<AudioItemViewModel> AudioItems { get; }
    
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
            var allDisplays = state.Display.ActiveTargets.ToList();
            
            // Добавляем также неактивные, но доступные дисплеи
            // (для этого нужно вызвать Detect напрямую, но пока используем ActiveTargets)
            
            foreach (var display in allDisplays)
            {
                var role = DisplayRole.None;
                
                if (adminConfig?.PanelDisplayOverride?.DisplayKey == display.Key)
                    role = DisplayRole.Panel;
                else if (adminConfig?.ComputerDisplayOverride?.DisplayKey == display.Key)
                    role = DisplayRole.Computer;
                
                DisplayItems.Add(new DisplayItemViewModel(display, role));
            }
            
            // Заполняем список аудиоустройств
            AudioItems.Clear();
            foreach (var endpoint in state.Audio.Endpoints)
            {
                var role = AudioRole.None;
                
                if (adminConfig?.PanelAudioEndpointIdOverride == endpoint.Id)
                    role = AudioRole.Panel;
                else if (adminConfig?.ComputerAudioEndpointIdOverride == endpoint.Id)
                    role = AudioRole.Computer;
                
                AudioItems.Add(new AudioItemViewModel(endpoint, role));
            }
            
            StatusMessage = $"Найдено дисплеев: {DisplayItems.Count}, аудиоустройств: {AudioItems.Count}";
        }
        catch (Exception ex)
        {
            _log.Error("Ошибка при обновлении списка устройств", ex);
            StatusMessage = "Ошибка при загрузке устройств: " + ex.Message;
        }
    }
    
    private void ExecuteSave()
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
                // Ищем выбранную панель
                var panelDisplay = DisplayItems.FirstOrDefault(d => d.Role == DisplayRole.Panel);
                if (panelDisplay != null)
                {
                    adminConfig.PanelDisplayOverride = new ManualDisplayMapping
                    {
                        DisplayKey = panelDisplay.Display.Key,
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
            
            StatusMessage = UseManualConfiguration 
                ? "✓ Ручная конфигурация сохранена. Перезапустите приложение для применения."
                : "✓ Автоопределение включено. Перезапустите приложение для применения.";
        }
        catch (Exception ex)
        {
            _log.Error("Ошибка при сохранении административной конфигурации", ex);
            StatusMessage = "Ошибка при сохранении: " + ex.Message;
        }
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
    
    public DisplayItemViewModel(DisplayTargetInfo display, DisplayRole role)
    {
        Display = display;
        _role = role;
    }
    
    public DisplayTargetInfo Display { get; }
    
    public DisplayRole Role
    {
        get => _role;
        set => SetProperty(ref _role, value);
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
    
    public AudioItemViewModel(AudioEndpointInfo endpoint, AudioRole role)
    {
        Endpoint = endpoint;
        _role = role;
    }
    
    public AudioEndpointInfo Endpoint { get; }
    
    public AudioRole Role
    {
        get => _role;
        set => SetProperty(ref _role, value);
    }
    
    public string AudioInfo => 
        $"{Endpoint.DisplayName} | {Endpoint.DeviceDescription} | " +
        $"{(Endpoint.IsDefault ? "✓ По умолчанию" : "")} | " +
        $"{(Endpoint.IsPanelCandidate ? $"Оценка панели: {Endpoint.PanelScore}" : "")}";
}
