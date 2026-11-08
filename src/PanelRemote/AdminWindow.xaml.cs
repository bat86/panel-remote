using System.Windows;
using PanelRemote.Services;
using PanelRemote.ViewModels;

namespace PanelRemote;

/// <summary>
/// Окно настроек администратора.
/// Позволяет вручную выбрать устройства для панели и компьютера.
/// </summary>
public partial class AdminWindow : Window
{
    public AdminWindow(AppController controller, SettingsStore settings, AppLog log)
    {
        InitializeComponent();
        
        var viewModel = new AdminViewModel(controller, settings, log);
        DataContext = viewModel;
        
        log.Info("Открыто окно администратора");
    }
}
