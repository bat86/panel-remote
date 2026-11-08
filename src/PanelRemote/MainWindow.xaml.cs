using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using PanelRemote.Interop;
using PanelRemote.Services;
using PanelRemote.ViewModels;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace PanelRemote;

public partial class MainWindow : Window
{
    private const int HotkeyDuplicate = 0x5001;
    private const int HotkeyPanelSound = 0x5002;
    private const int HotkeyAdmin = 0x5003;
    private const uint ModNoRepeat = 0x4000;
    private const int WM_POWERBROADCAST = 0x0218;
    private const int PBT_APMRESUMESUSPEND = 0x0007;
    private const int PBT_APMRESUMEAUTOMATIC = 0x0012;

    private readonly MainViewModel _viewModel;
    private readonly SettingsStore _settings;
    private readonly AppLog _log;
    private readonly bool _startHidden;
    private readonly DispatcherTimer _refreshDebounce;
    private readonly DispatcherTimer _pollTimer;

    private HwndSource? _hwndSource;
    private IntPtr _hwnd;
    private uint _showMessage;
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ToolStripMenuItem? _topmostMenuItem;
    private Drawing.Icon? _windowIcon;
    private bool _ownsWindowIcon;
    private bool _reallyExit;
    private bool _hotkeyDuplicateRegistered;
    private bool _hotkeySoundRegistered;
    private bool _hotkeyAdminRegistered;
    private bool _refreshing;

    public MainWindow(
        MainViewModel viewModel,
        SettingsStore settings,
        AppLog log,
        bool startHidden)
    {
        _viewModel = viewModel;
        _settings = settings;
        _log = log;
        _startHidden = startHidden;

        InitializeComponent();
        DataContext = viewModel;
        Topmost = settings.Current.AlwaysOnTop;
        Opacity = 0;

        _refreshDebounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(700)
        };
        _refreshDebounce.Tick += RefreshDebounce_Tick;

        _pollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _pollTimer.Tick += PollTimer_Tick;

        Loaded += MainWindow_Loaded;
        SourceInitialized += MainWindow_SourceInitialized;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
        StateChanged += MainWindow_StateChanged;
        DpiChanged += (_, _) => ApplyWorkArea();
    }

    public void HideToTray()
    {
        if (_trayIcon is null)
        {
            // Если трей недоступен, окно нельзя терять.
            WindowState = WindowState.Minimized;
            return;
        }

        if (!IsVisible)
        {
            ShowInTaskbar = false;
            return;
        }

        ShowInTaskbar = false;
        Hide();
        _log.Info("Окно свёрнуто в трей.");
    }

    public void ShowFromTray()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(ShowFromTray), DispatcherPriority.Normal);
            return;
        }

        ShowInTaskbar = true;
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        ApplyWorkArea();
        Activate();
        var handle = new WindowInteropHelper(this).Handle;
        NativeMethods.ShowWindow(handle, NativeMethods.SW_RESTORE);
        NativeMethods.SetForegroundWindow(handle);
        DuplicateTile.FocusTile();
    }

    public void PrepareForExit()
    {
        _reallyExit = true;
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
        }
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyWorkArea();
        EnsureTrayIcon();
        _pollTimer.Start();

        if (_startHidden && _trayIcon is not null)
        {
            HideToTray();
            Opacity = 1;
        }
        else
        {
            ShowInTaskbar = true;
            Opacity = 1;
            DuplicateTile.FocusTile();
        }
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProc);

        _showMessage = SingleInstance.ShowMessage;

        try
        {
            _hotkeyDuplicateRegistered = NativeMethods.RegisterHotKey(
                _hwnd,
                HotkeyDuplicate,
                ModNoRepeat,
                NativeMethods.VK_F8);
            _hotkeySoundRegistered = NativeMethods.RegisterHotKey(
                _hwnd,
                HotkeyPanelSound,
                ModNoRepeat,
                NativeMethods.VK_F9);
            _hotkeyAdminRegistered = NativeMethods.RegisterHotKey(
                _hwnd,
                HotkeyAdmin,
                ModNoRepeat | NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT,
                NativeMethods.VK_A);

            if (!_hotkeyDuplicateRegistered)
            {
                _log.Warn("Не удалось зарегистрировать глобальную клавишу F8 (возможно, занята).");
            }

            if (!_hotkeySoundRegistered)
            {
                _log.Warn("Не удалось зарегистрировать глобальную клавишу F9 (возможно, занята).");
            }

            if (!_hotkeyAdminRegistered)
            {
                _log.Warn("Не удалось зарегистрировать горячую клавишу Ctrl+Shift+A для администратора (возможно, занята).");
            }

            if (_hotkeyDuplicateRegistered || _hotkeySoundRegistered)
            {
                _log.Info("Глобальные клавиши: F8 — дублировать, F9 — звук на панель.");
            }
            
            if (_hotkeyAdminRegistered)
            {
                _log.Info("Горячая клавиша администратора: Ctrl+Shift+A — настройки устройств.");
            }
        }
        catch (Exception ex)
        {
            _log.Error("Ошибка регистрации горячих клавиш", ex);
        }

        _log.Info("Главное окно создано.");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_showMessage != 0 && msg == unchecked((int)_showMessage))
        {
            ShowFromTray();
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == NativeMethods.WM_HOTKEY)
        {
            var id = wParam.ToInt32();
            if (id == HotkeyDuplicate)
            {
                Dispatcher.BeginInvoke(new Action(_viewModel.ExecuteDuplicateShortcut));
                handled = true;
            }
            else if (id == HotkeyPanelSound)
            {
                Dispatcher.BeginInvoke(new Action(_viewModel.ExecutePanelAudioShortcut));
                handled = true;
            }
            else if (id == HotkeyAdmin)
            {
                Dispatcher.BeginInvoke(new Action(OpenAdminWindow));
                handled = true;
            }

            return IntPtr.Zero;
        }

        if (msg == NativeMethods.WM_DEVICECHANGE || msg == NativeMethods.WM_DISPLAYCHANGE)
        {
            ScheduleRefresh();
        }
        else if (msg == WM_POWERBROADCAST
                 && (wParam.ToInt32() == PBT_APMRESUMESUSPEND || wParam.ToInt32() == PBT_APMRESUMEAUTOMATIC))
        {
            _log.Info("Компьютер вышел из сна — перечитываю HDMI и звук.");
            ScheduleRefresh();
        }

        return IntPtr.Zero;
    }

    private void ScheduleRefresh()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(ScheduleRefresh), DispatcherPriority.Background);
            return;
        }

        _refreshDebounce.Stop();
        _refreshDebounce.Start();
    }

    private async void RefreshDebounce_Tick(object? sender, EventArgs e)
    {
        _refreshDebounce.Stop();
        await RefreshFromEnvironmentAsync();
    }

    private async void PollTimer_Tick(object? sender, EventArgs e)
    {
        await RefreshFromEnvironmentAsync();
    }

    private async Task RefreshFromEnvironmentAsync()
    {
        if (_refreshing || _reallyExit)
        {
            return;
        }

        _refreshing = true;
        try
        {
            await _viewModel.RefreshAsync();
            if (IsVisible)
            {
                ApplyWorkAreaIfNeeded();
            }
        }
        catch (Exception ex)
        {
            _log.Error("Ошибка фонового обновления", ex);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ApplyWorkArea()
    {
        if (WindowState != WindowState.Normal)
        {
            WindowState = WindowState.Normal;
        }

        var area = SystemParameters.WorkArea;
        Left = area.Left;
        Top = area.Top;
        Width = Math.Max(MinWidth, area.Width);
        Height = Math.Max(MinHeight, area.Height);
    }

    private void ApplyWorkAreaIfNeeded()
    {
        if (WindowState != WindowState.Normal || !IsVisible)
        {
            return;
        }

        var area = SystemParameters.WorkArea;
        if (Math.Abs(Left - area.Left) > 1
            || Math.Abs(Top - area.Top) > 1
            || Math.Abs(Width - Math.Max(MinWidth, area.Width)) > 1
            || Math.Abs(Height - Math.Max(MinHeight, area.Height)) > 1)
        {
            ApplyWorkArea();
        }
    }

    private void EnsureTrayIcon()
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = true;
            return;
        }

        try
        {
            _windowIcon = LoadWindowIcon();
            var menu = new Forms.ContextMenuStrip
            {
                ShowImageMargin = false,
                Font = new Drawing.Font("Segoe UI", 10.5f)
            };

            var openItem = new Forms.ToolStripMenuItem("Открыть «Пульт доски»");
            openItem.Click += (_, _) => ShowFromTray();

            _topmostMenuItem = new Forms.ToolStripMenuItem("Поверх всех окон")
            {
                CheckOnClick = true,
                Checked = _settings.Current.AlwaysOnTop
            };
            _topmostMenuItem.CheckedChanged += TopmostMenuItem_CheckedChanged;

            var adminItem = new Forms.ToolStripMenuItem("Настройки администратора (Ctrl+Shift+A)");
            adminItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(OpenAdminWindow));

            var exitItem = new Forms.ToolStripMenuItem("Выход");
            exitItem.Click += (_, _) => ExitApplication();

            menu.Items.Add(openItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(_topmostMenuItem);
            menu.Items.Add(adminItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(exitItem);

            _trayIcon = new Forms.NotifyIcon
            {
                Icon = _windowIcon,
                Text = "Пульт доски",
                Visible = true,
                ContextMenuStrip = menu
            };
            _trayIcon.MouseClick += TrayIcon_MouseClick;
            _trayIcon.DoubleClick += (_, _) => ShowFromTray();
            _log.Info("Иконка в трее создана.");
        }
        catch (Exception ex)
        {
            _log.Error("Не удалось создать иконку в трее", ex);
        }
    }

    private void TrayIcon_MouseClick(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Left)
        {
            ShowFromTray();
        }
    }

    private void TopmostMenuItem_CheckedChanged(object? sender, EventArgs e)
    {
        try
        {
            var enabled = _topmostMenuItem?.Checked == true;
            Topmost = enabled;
            _settings.Current.AlwaysOnTop = enabled;
            _settings.Save();
            _log.Info($"Режим «поверх всех окон»: {enabled}.");
        }
        catch (Exception ex)
        {
            _log.Error("Не удалось изменить режим поверх всех окон", ex);
        }
    }

    private void MinimizeToTray_Click(object sender, RoutedEventArgs e)
    {
        HideToTray();
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            HideToTray();
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_reallyExit)
        {
            if (_trayIcon is not null)
            {
                e.Cancel = true;
                HideToTray();
            }
            else
            {
                _reallyExit = true;
            }
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _pollTimer.Stop();
        _refreshDebounce.Stop();

        if (_hotkeyDuplicateRegistered)
        {
            NativeMethods.UnregisterHotKey(_hwnd, HotkeyDuplicate);
        }

        if (_hotkeySoundRegistered)
        {
            NativeMethods.UnregisterHotKey(_hwnd, HotkeyPanelSound);
        }

        if (_hotkeyAdminRegistered)
        {
            NativeMethods.UnregisterHotKey(_hwnd, HotkeyAdmin);
        }

        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        if (_windowIcon is not null && _ownsWindowIcon)
        {
            _windowIcon.Dispose();
        }

        _windowIcon = null;
        _log.Info("Главное окно закрыто.");
    }

    private void OpenAdminWindow()
    {
        try
        {
            _log.Info("Открытие окна администратора по горячей клавише Ctrl+Shift+A");
            
            // Получаем необходимые зависимости из App
            var app = System.Windows.Application.Current as App;
            if (app == null)
            {
                _log.Error("Не удалось получить экземпляр приложения");
                return;
            }
            
            // Создаём и показываем окно администратора
            // Передаём AppController через доступ к MainViewModel
            var controller = _viewModel.GetController();
            var adminWindow = new AdminWindow(controller, _settings, _log);
            adminWindow.Owner = this;
            adminWindow.ShowDialog();
        }
        catch (Exception ex)
        {
            _log.Error("Ошибка при открытии окна администратора", ex);
            MessageBox.Show(
                "Не удалось открыть окно администратора.\n\n" + ex.Message,
                "Пульт доски",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ExitApplication()
    {
        PrepareForExit();
        System.Windows.Application.Current.Shutdown();
    }

    private Drawing.Icon LoadWindowIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/app.ico"));
            if (resource?.Stream is not null)
            {
                using var stream = resource.Stream;
                _ownsWindowIcon = true;
                return new Drawing.Icon(stream);
            }
        }
        catch
        {
            // попробуем иконку exe
        }

        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var icon = Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon is not null)
                {
                    _ownsWindowIcon = true;
                    return icon;
                }
            }
        }
        catch
        {
            // ниже будет системная
        }

        _ownsWindowIcon = false;
        return Drawing.SystemIcons.Application;
    }
}
