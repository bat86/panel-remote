using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PanelRemote.Controls;

public partial class ActionTile : UserControl
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon),
        typeof(string),
        typeof(ActionTile),
        new PropertyMetadata("•"));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(ActionTile),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
        nameof(Hint),
        typeof(string),
        typeof(ActionTile),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(
        nameof(Command),
        typeof(ICommand),
        typeof(ActionTile),
        new PropertyMetadata(null));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive),
        typeof(bool),
        typeof(ActionTile),
        new PropertyMetadata(false));

    public static readonly DependencyProperty IsPrimaryProperty = DependencyProperty.Register(
        nameof(IsPrimary),
        typeof(bool),
        typeof(ActionTile),
        new PropertyMetadata(false));

    public static readonly DependencyProperty IsDiagnosticProperty = DependencyProperty.Register(
        nameof(IsDiagnostic),
        typeof(bool),
        typeof(ActionTile),
        new PropertyMetadata(false));

    public ActionTile()
    {
        InitializeComponent();
    }

    public string Icon
    {
        get => (string)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Hint
    {
        get => (string)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public bool IsPrimary
    {
        get => (bool)GetValue(IsPrimaryProperty);
        set => SetValue(IsPrimaryProperty, value);
    }

    public bool IsDiagnostic
    {
        get => (bool)GetValue(IsDiagnosticProperty);
        set => SetValue(IsDiagnosticProperty, value);
    }

    public void FocusTile()
    {
        TileButton.Focus();
        Keyboard.Focus(TileButton);
    }
}
