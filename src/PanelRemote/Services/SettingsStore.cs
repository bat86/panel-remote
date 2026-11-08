using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PanelRemote.Models;

namespace PanelRemote.Services;

/// <summary>
/// Ручное переопределение дисплея (панель или компьютер).
/// </summary>
public sealed class ManualDisplayMapping
{
    /// <summary>Уникальный ключ дисплея (DisplayTargetInfo.Key)</summary>
    public string DisplayKey { get; set; } = string.Empty;
    
    /// <summary>Человекочитаемое имя для проверки (на случай изменения конфигурации)</summary>
    public string DisplayFriendlyName { get; set; } = string.Empty;
    
    /// <summary>Тип подключения для дополнительной валидации</summary>
    public string ConnectionType { get; set; } = string.Empty;
}

/// <summary>
/// Административная конфигурация для ручного выбора устройств.
/// Используется техподдержкой в случаях, когда автоопределение не работает.
/// </summary>
public sealed class AdminConfiguration
{
    /// <summary>Режим: использовать ручные настройки (true) или автоопределение (false)</summary>
    public bool UseManualConfiguration { get; set; }
    
    /// <summary>Ручное переопределение: какой дисплей считать панелью</summary>
    public ManualDisplayMapping? PanelDisplayOverride { get; set; }
    
    /// <summary>Ручное переопределение: какой дисплей считать компьютером</summary>
    public ManualDisplayMapping? ComputerDisplayOverride { get; set; }
    
    /// <summary>Ручное переопределение: ID аудиоустройства панели</summary>
    public string? PanelAudioEndpointIdOverride { get; set; }
    
    /// <summary>Ручное переопределение: ID аудиоустройства компьютера</summary>
    public string? ComputerAudioEndpointIdOverride { get; set; }
}

public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public bool HasSavedPreferences { get; set; }
    public DisplayMode? LastDisplayMode { get; set; }
    public AudioTarget? LastAudioTarget { get; set; }
    public string? PanelAudioEndpointId { get; set; }
    public string? ComputerAudioEndpointId { get; set; }
    public bool AlwaysOnTop { get; set; }
    public bool AutoRestore { get; set; } = true;
    
    /// <summary>Административная конфигурация (доступна через режим администратора)</summary>
    public AdminConfiguration? AdminConfig { get; set; }
}

public sealed class SettingsStore
{
    private readonly object _gate = new();
    private readonly AppLog _log;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public SettingsStore(AppLog log)
    {
        _log = log;
        Current = new AppSettings();
    }

    public AppSettings Current { get; private set; }

    public void Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(AppPaths.SettingsFile))
                {
                    Current = new AppSettings();
                    return;
                }

                var json = File.ReadAllText(AppPaths.SettingsFile);
                Current = JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions) ?? new AppSettings();
                
                var configMode = Current.AdminConfig?.UseManualConfiguration == true ? "ручная" : "автоопределение";
                _log.Info($"Настройки загружены: режим={Current.LastDisplayMode?.ToString() ?? "нет"}, " +
                          $"звук={Current.LastAudioTarget?.ToString() ?? "нет"}, " +
                          $"конфигурация={configMode}");
            }
            catch (Exception ex)
            {
                _log.Error("Не удалось прочитать settings.json, используются значения по умолчанию", ex);
                TryPreserveBrokenFile();
                Current = new AppSettings();
            }
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            try
            {
                AppPaths.EnsureDataDirectories();
                var json = JsonSerializer.Serialize(Current, _jsonOptions);
                var temp = AppPaths.SettingsFile + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, AppPaths.SettingsFile, overwrite: true);
            }
            catch (Exception ex)
            {
                _log.Error("Не удалось сохранить settings.json", ex);
            }
        }
    }

    private void TryPreserveBrokenFile()
    {
        try
        {
            var broken = AppPaths.SettingsFile + $".bad-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(AppPaths.SettingsFile, broken, overwrite: true);
            _log.Warn($"Повреждённые настройки сохранены как {broken}");
        }
        catch
        {
            // не критично
        }
    }
}
