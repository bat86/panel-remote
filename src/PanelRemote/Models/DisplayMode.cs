namespace PanelRemote.Models;

/// <summary>Четыре режима, знакомые пользователю по Win+P.</summary>
public enum DisplayMode
{
    ComputerOnly,
    Duplicate,
    Extend,
    PanelOnly
}

public static class DisplayModeNames
{
    public static string Human(this DisplayMode mode) => mode switch
    {
        DisplayMode.ComputerOnly => "только компьютер",
        DisplayMode.Duplicate => "повтор",
        DisplayMode.Extend => "расширить",
        DisplayMode.PanelOnly => "только панель",
        _ => "неизвестно"
    };

    public static string Short(this DisplayMode mode) => mode switch
    {
        DisplayMode.ComputerOnly => "Только компьютер",
        DisplayMode.Duplicate => "Повтор",
        DisplayMode.Extend => "Расширить",
        DisplayMode.PanelOnly => "Только панель",
        _ => "Неизвестно"
    };
}
