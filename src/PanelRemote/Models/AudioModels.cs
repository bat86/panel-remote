using System.Collections.Generic;

namespace PanelRemote.Models;

public sealed record AudioEndpointInfo(
    string Id,
    string FriendlyName,
    string DeviceDescription,
    bool IsDefault,
    bool IsPanelCandidate,
    int PanelScore,
    string StateText)
{
    public string DisplayName => string.IsNullOrWhiteSpace(FriendlyName)
        ? "Аудиоустройство без имени"
        : FriendlyName;
}

public sealed record AudioSnapshot(
    IReadOnlyList<AudioEndpointInfo> Endpoints,
    AudioEndpointInfo? DefaultEndpoint,
    AudioEndpointInfo? PanelEndpoint,
    AudioEndpointInfo? ComputerEndpoint,
    AudioTarget CurrentTarget,
    bool PanelAudioAvailable)
{
    public string DefaultName => DefaultEndpoint?.DisplayName ?? "устройство не выбрано";
}

public sealed record AudioApplyResult(
    bool Success,
    AudioTarget Target,
    AudioSnapshot Snapshot,
    string Message,
    string? EndpointId = null);
