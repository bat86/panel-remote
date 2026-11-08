namespace PanelRemote.Models;

public sealed record AppState(
    DisplaySnapshot Display,
    AudioSnapshot Audio,
    bool PanelConnected,
    bool IsInitialized,
    string? Notice);
