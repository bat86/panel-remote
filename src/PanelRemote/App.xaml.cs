using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using PanelRemote.Models;
using PanelRemote.Services;
using PanelRemote.ViewModels;

namespace PanelRemote;

public partial class App : Application
{
    private AppLog? _log;
    private AppController? _controller;
    private SingleInstance? _singleInstance;
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _log = new AppLog();
        _log.Info($"=== Запуск {AppPaths.AppDisplayName} {AppPaths.Version} ===");
        _log.Info($"Windows: {Environment.OSVersion}; 64-bit: {Environment.Is64BitOperatingSystem}; " +
                  $"пользователь: {Environment.UserName}; exe: {AppPaths.CurrentExecutable}");

        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        DispatcherUnhandledException += App_DispatcherUnhandledException;

        var args = new HashSet<string>(
            e.Args.Select(a => a.Trim().ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);

        if (args.Contains("--uninstall"))
        {
            _log.Info("Режим удаления.");
            InstallService.Uninstall(_log);
            Shutdown();
            return;
        }

        if (args.Contains("--install"))
        {
            _log.Info("Режим тихой установки.");
            var result = InstallService.Install(_log);
            if (!result.Success)
            {
                _log.Error("Тихая установка не удалась: " + result.Error);
            }

            Shutdown();
            return;
        }

        var portable = args.Contains("--portable");
        if (!portable && !Debugger.IsAttached && !AppPaths.IsInstalledLocation)
        {
            // Первый запуск одним файлом: тихо раскладываемся в профиль пользователя.
            // Права администратора не нужны.
            if (!InstallService.TryAutoInstall(_log, out var installError))
            {
                _log.Warn("Автоустановка не удалась, продолжаю запуск из текущей папки: " + installError);
            }
        }
        else if (!portable && AppPaths.IsInstalledLocation)
        {
            InstallService.EnsureAutostart(_log);
        }

        try
        {
            _singleInstance = new SingleInstance();
            if (!_singleInstance.IsFirst)
            {
                _log.Info("Программа уже запущена — показываю существующее окно и выхожу.");
                SingleInstance.NotifyExisting();
                Shutdown();
                return;
            }
        }
        catch (Exception ex)
        {
            // Не оставляем учителя без программы из-за политики доступа к именованному объекту.
            _log.Error("Не удалось проверить единственный экземпляр, продолжаю запуск", ex);
        }

        var settings = new SettingsStore(_log);
        settings.Load();

        var display = new DisplayService(_log);
        var audio = new AudioService(_log);
        _controller = new AppController(display, audio, settings, _log);
        var viewModel = new MainViewModel(_controller, _log);

        var startHidden = args.Contains("--tray") || args.Contains("--autostart");
        _mainWindow = new MainWindow(viewModel, settings, _log, startHidden);
        MainWindow = _mainWindow;

        _log.Info($"Режим запуска: {(startHidden ? "в трее" : "с окном")}, portable={portable}.");
        _mainWindow.Show();

        Dispatcher.BeginInvoke(
            new Action(async () =>
            {
                try
                {
                    await _controller.InitializeAsync();
                }
                catch (Exception ex)
                {
                    _log?.Error("Ошибка начальной инициализации", ex);
                }
            }),
            DispatcherPriority.Background);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _log?.Info("Windows завершает сеанс.");
        _mainWindow?.PrepareForExit();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _controller?.Dispose();
        }
        catch
        {
            // игнорируем
        }

        try
        {
            _singleInstance?.Dispose();
        }
        catch
        {
            // игнорируем
        }

        _log?.Info("=== Выход ===");
        _log?.Dispose();
        base.OnExit(e);
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Error("Необработанная ошибка интерфейса", e.Exception);
        try
        {
            MessageBox.Show(
                "Пульт доски столкнулся с ошибкой.\n\n" +
                "Закройте программу и откройте снова. Если ошибка повторится — " +
                "покажите техническому специалисту журнал из папки:\n" +
                AppPaths.LogsDirectory,
                "Пульт доски",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // не показываем второй раз
        }

        e.Handled = true;
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _log?.Error("Необработанная фоновая ошибка", e.Exception);
        e.SetObserved();
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        _log?.Error("Необработанная ошибка процесса", e.ExceptionObject as Exception);
    }
}
