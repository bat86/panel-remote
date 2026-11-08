using System.Collections.Generic;

namespace PanelRemote.Models;

public enum DisplayConnectionKind
{
    Unknown,
    Hdmi,
    DisplayPort,
    UsbC,
    Dvi,
    Vga,
    Internal,
    Other
}

public sealed record DisplayTargetInfo(
    string Key,
    uint AdapterLow,
    int AdapterHigh,
    uint TargetId,
    uint SourceId,
    string GdiName,
    string FriendlyName,
    string DevicePath,
    DisplayConnectionKind Connection,
    bool Active,
    bool Available,
    bool IsInternal,
    bool IsPrimary,
    uint Width,
    uint Height,
    int RefreshHz,
    bool HasSeparateDesktopSource)
{
    public string DisplayName => string.IsNullOrWhiteSpace(FriendlyName)
        ? "Дисплей без имени"
        : FriendlyName;

    public string ConnectionName => Connection switch
    {
        DisplayConnectionKind.Hdmi => "HDMI",
        DisplayConnectionKind.DisplayPort => "DisplayPort",
        DisplayConnectionKind.UsbC => "USB-C",
        DisplayConnectionKind.Dvi => "DVI",
        DisplayConnectionKind.Vga => "VGA",
        DisplayConnectionKind.Internal => "встроенный экран",
        _ => "видеовыход"
    };

    public string ResolutionText => Width > 0 && Height > 0
        ? $"{Width} × {Height}"
        : "разрешение не определено";
}

public sealed record DisplaySnapshot(
    DisplayMode CurrentMode,
    DisplayTargetInfo? Panel,
    DisplayTargetInfo? Computer,
    IReadOnlyList<DisplayTargetInfo> ActiveTargets,
    string ResolutionText,
    int RefreshHz,
    bool IsCloneTopology,
    string? Problem)
{
    public bool PanelConnected => Panel is not null && (Panel.Active || Panel.Available);
    public bool PanelActive => Panel?.Active == true;
    public bool ComputerActive => Computer?.Active == true;
    public bool HasTwoDisplays => Panel is not null && Computer is not null
        && !string.Equals(Panel.Key, Computer.Key, System.StringComparison.OrdinalIgnoreCase);
    public bool IsHealthy => PanelConnected && CurrentMode switch
    {
        DisplayMode.Duplicate => PanelActive && ComputerActive && IsCloneTopology,
        DisplayMode.Extend => PanelActive && ComputerActive,
        DisplayMode.PanelOnly => PanelActive,
        DisplayMode.ComputerOnly => ComputerActive,
        _ => false
    };
}

public sealed record DisplayApplyResult(bool Success, DisplayMode Mode, DisplaySnapshot Snapshot, string Message);
