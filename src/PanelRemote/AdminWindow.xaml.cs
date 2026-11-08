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

        // Окно занимает всю высоту рабочего стола: списков устройств бывает много.
        var workArea = SystemParameters.WorkArea;
        Height = workArea.Height - 24;
        MaxHeight = workArea.Height - 24;
        Top = workArea.Top + 12;
        
        log.Info("Открыто окно администратора");
    }
}
